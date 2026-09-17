using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal sealed record StandingLeaseApprovalDetails(
    string DisplayName,
    AuthorizedPhysicalRectangle DisplayBounds,
    AuthorizedPhysicalRectangle RegionWithinDisplay,
    DateTimeOffset ScheduledStartUtc,
    DateTimeOffset LatestStartUtc,
    DateTimeOffset PlannedEndUtc,
    TimeSpan MaximumDuration,
    DateTimeOffset LeaseValidUntilUtc,
    string OutputDirectory,
    string FrozenFileName);

/// <summary>
/// Dedicated local approval surface for a standing lease. It captures only a
/// temporary in-memory preview for this form and never serializes it.
/// </summary>
internal sealed class StandingLeaseApprovalForm : Form
{
    private readonly Button _approveButton;
    private readonly Button _rejectButton;
    private readonly PictureBox _previewBox;
    private readonly Label _previewFallbackLabel;
    private readonly Label _previewBoundsLabel;
    private readonly TextBox _detailsBox;
    private readonly Label _shortcutLabel;
    private readonly IUiTextProvider _text;
    private Bitmap? _previewBitmap;

    internal StandingLeaseApprovalForm(
        StandingLeaseApprovalDetails details,
        IScreenPreviewProvider? previewProvider = null,
        IUiTextProvider? textProvider = null)
    {
        ArgumentNullException.ThrowIfNull(details);

        _text = textProvider ?? new UiTextProvider(UiLanguageStore.LoadOrDefault());
        var provider = previewProvider ?? new GdiScreenPreviewProvider();

        Text = _text.Get("StandingLeaseApproval_Title");
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(620, 700);
        ClientSize = new Size(820, 820);
        BackColor = Color.FromArgb(30, 33, 38);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 7,
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            AutoSize = true,
            Text = _text.Get("StandingLeaseApproval_Heading"),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(125, 211, 252),
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(title, 0, 0);

        var warning = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = _text.Get("StandingLeaseApproval_Warning"),
            ForeColor = Color.FromArgb(253, 186, 116),
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(warning, 0, 1);

        var previewLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.FromArgb(22, 24, 28),
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 12),
        };
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var previewTitle = new Label
        {
            AutoSize = true,
            Text = _text.Get("StandingLeaseApproval_PreviewTitle"),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 0, 6),
        };
        previewLayout.Controls.Add(previewTitle, 0, 0);
        _previewBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Black,
            BorderStyle = BorderStyle.FixedSingle,
        };
        previewLayout.Controls.Add(_previewBox, 0, 1);
        _previewFallbackLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            ForeColor = Color.FromArgb(253, 186, 116),
            Margin = new Padding(0, 6, 0, 0),
            Visible = false,
        };
        previewLayout.Controls.Add(_previewFallbackLabel, 0, 2);
        root.Controls.Add(previewLayout, 0, 2);

        _previewBoundsLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = _text.Format("StandingLeaseApproval_PreviewBounds", GetAbsoluteRegion(details).X, GetAbsoluteRegion(details).Y, GetAbsoluteRegion(details).Width, GetAbsoluteRegion(details).Height),
            ForeColor = Color.Silver,
            Margin = new Padding(0, 0, 0, 10),
        };

        _detailsBox = new TextBox
        {
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(22, 24, 28),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Text = FormatDetails(details),
            Margin = new Padding(0, 0, 0, 12),
        };
        var detailsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = BackColor,
        };
        detailsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        detailsLayout.Controls.Add(_previewBoundsLabel, 0, 0);
        detailsLayout.Controls.Add(_detailsBox, 0, 1);
        root.Controls.Add(detailsLayout, 0, 3);

        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = _text.Get("StandingLeaseApproval_Note"),
            ForeColor = Color.Gainsboro,
            Margin = new Padding(0, 0, 0, 14),
        };
        root.Controls.Add(note, 0, 4);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            WrapContents = false,
        };
        _approveButton = new Button
        {
            AutoSize = true,
            Text = _text.Get("StandingLeaseApproval_Approve"),
            BackColor = Color.FromArgb(22, 163, 74),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.OK,
            Padding = new Padding(14, 7, 14, 7),
            Margin = new Padding(8, 0, 0, 0),
        };
        _rejectButton = new Button
        {
            AutoSize = true,
            Text = _text.Get("StandingLeaseApproval_Reject"),
            BackColor = Color.FromArgb(185, 28, 28),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel,
            Padding = new Padding(14, 7, 14, 7),
        };
        buttons.Controls.Add(_approveButton);
        buttons.Controls.Add(_rejectButton);
        root.Controls.Add(buttons, 0, 5);

        _shortcutLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Text = _text.Get("StandingLeaseApproval_Keyboard"),
            ForeColor = Color.Gray,
            Margin = new Padding(0, 10, 0, 0),
        };
        root.Controls.Add(_shortcutLabel, 0, 6);

        // Enter is deliberately a safe rejection, never an approval shortcut.
        AcceptButton = _rejectButton;
        CancelButton = _rejectButton;
        BuildPreview(details, provider);
        Shown += (_, _) =>
        {
            _rejectButton.Focus();
        };
    }

    internal bool HasPreviewImageForTests => _previewBox.Image is not null;
    internal string PreviewFallbackTextForTests => _previewFallbackLabel.Text;
    internal string PreviewBoundsTextForTests => _previewBoundsLabel.Text;
    internal bool AcceptButtonIsRejectForTests => ReferenceEquals(AcceptButton, _rejectButton);
    internal bool CancelButtonIsRejectForTests => ReferenceEquals(CancelButton, _rejectButton);
    internal DialogResult ApproveButtonResultForTests => _approveButton.DialogResult;
    internal DialogResult RejectButtonResultForTests => _rejectButton.DialogResult;
    internal string KeyboardTextForTests => _shortcutLabel.Text;
    internal string DetailsTextForTests => _detailsBox.Text;
    internal ScrollBars DetailsScrollBarsForTests => _detailsBox.ScrollBars;
    internal Rectangle AbsolutePreviewBoundsForTests { get; private set; }
    internal bool PreviewBitmapDisposedForTests { get; private set; }

    internal static bool ShowModal(
        StandingLeaseApprovalDetails details,
        IScreenPreviewProvider? previewProvider = null,
        IUiTextProvider? textProvider = null,
        CancellationToken cancellationToken = default)
    {
        using var form = new StandingLeaseApprovalForm(details, previewProvider, textProvider);
        var cancellationRequested = 0;
        using var registration = cancellationToken.Register(() =>
        {
            Interlocked.Exchange(ref cancellationRequested, 1);
            try
            {
                if (form.IsHandleCreated)
                    form.BeginInvoke(new Action(form.Close));
            }
            catch (InvalidOperationException)
            {
                // Shown handler below closes it if cancellation happened early.
            }
        });
        form.Shown += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested)
                form.Close();
        };
        var result = form.ShowDialog();
        return Volatile.Read(ref cancellationRequested) == 0 &&
               !cancellationToken.IsCancellationRequested &&
               result == DialogResult.OK;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_previewBox?.Image is not null)
                _previewBox.Image = null;
            _previewBitmap?.Dispose();
            _previewBitmap = null;
            PreviewBitmapDisposedForTests = true;
        }
        base.Dispose(disposing);
    }

    private void BuildPreview(StandingLeaseApprovalDetails details, IScreenPreviewProvider provider)
    {
        try
        {
            var absolute = GetAbsoluteRegion(details);
            AbsolutePreviewBoundsForTests = new Rectangle(absolute.X, absolute.Y, absolute.Width, absolute.Height);
            var captureBounds = new ConfirmationCaptureBounds(absolute.X, absolute.Y, absolute.Width, absolute.Height);
            var preview = ConfirmationPreviewBuilder.TryBuildPreview(
                captureBounds,
                provider,
                new Size(760, 260),
                out _);
            if (preview is not null)
            {
                _previewBitmap = preview;
                _previewBox.Image = preview;
                return;
            }
        }
        catch
        {
            // The fallback below is the safe, visible path for any preview failure.
        }

        _previewFallbackLabel.Text = _text.Get("StandingLeaseApproval_PreviewFallback");
        _previewFallbackLabel.Visible = true;
    }

    private string FormatDetails(StandingLeaseApprovalDetails details)
    {
        var display = details.DisplayBounds;
        var region = details.RegionWithinDisplay;
        return _text.Format(
            "StandingLeaseApproval_Details",
            details.DisplayName,
            display.X,
            display.Y,
            display.Width,
            display.Height,
            region.X,
            region.Y,
            region.Width,
            region.Height,
            details.ScheduledStartUtc.ToString("O"),
            details.LatestStartUtc.ToString("O"),
            details.PlannedEndUtc.ToString("O"),
            details.MaximumDuration.TotalSeconds.ToString("0"),
            details.LeaseValidUntilUtc.ToString("O"),
            details.OutputDirectory,
            details.FrozenFileName);
    }

    private static ConfirmationCaptureBounds GetAbsoluteRegion(StandingLeaseApprovalDetails details)
    {
        var display = details.DisplayBounds;
        var region = details.RegionWithinDisplay;
        return new ConfirmationCaptureBounds(
            checked(display.X + region.X),
            checked(display.Y + region.Y),
            region.Width,
            region.Height);
    }
}
