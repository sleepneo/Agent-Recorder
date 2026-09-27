using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using AgentRecorder.Core.Automation;

namespace AgentRecorder.App;

/// <summary>
/// Text-only approval to create a required-mode plan. This form deliberately
/// does not instantiate a screen preview or any capture-related provider.
/// </summary>
internal sealed class RequiredPlanCreationApprovalForm : Form
{
    private RequiredOnceCreationApprovalResult _result = RequiredOnceCreationApprovalResult.Rejected;

    internal RequiredPlanCreationApprovalForm(RequiredOnceCreationApprovalDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Text = "确认创建一次性计划 / Approve one-time plan";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(620, 500);
        ClientSize = new Size(760, 590);
        BackColor = Color.FromArgb(30, 33, 38);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 4,
            BackColor = BackColor,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var heading = new Label
        {
            AutoSize = true,
            Text = "本地批准：只创建计划，不开始录制 / Local approval: create a plan only; recording will not start",
            MaximumSize = new Size(700, 0),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = Color.FromArgb(125, 211, 252),
            Margin = new Padding(0, 0, 0, 14),
        };
        layout.Controls.Add(heading, 0, 0);

        var warning = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Text = "批准后，此计划仅进入已排程状态。到时不会自动开始；届时还必须由本机用户进行第二次确认。拒绝或关闭此窗口不会创建可执行计划。\r\nAfter approval, the plan is only scheduled. It will not start automatically; a second local confirmation is required at the scheduled time. Rejecting or closing this dialog creates no runnable plan.",
            ForeColor = Color.FromArgb(253, 186, 116),
            Margin = new Padding(0, 0, 0, 14),
        };
        layout.Controls.Add(warning, 0, 1);

        var detailsBox = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BackColor = Color.FromArgb(22, 24, 28),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            Text = FormatDetails(details),
            Margin = new Padding(0, 0, 0, 18),
        };
        layout.Controls.Add(detailsBox, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        var reject = new Button { Text = "拒绝 / Reject", AutoSize = true, MinimumSize = new Size(130, 40), DialogResult = DialogResult.Cancel };
        reject.Click += (_, _) => _result = RequiredOnceCreationApprovalResult.Rejected;
        var approve = new Button
        {
            Text = "批准创建计划 / Approve plan",
            AutoSize = true,
            MinimumSize = new Size(210, 40),
            DialogResult = DialogResult.OK,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        approve.FlatAppearance.BorderSize = 0;
        approve.Click += (_, _) => _result = RequiredOnceCreationApprovalResult.Approved;
        buttons.Controls.Add(reject);
        buttons.Controls.Add(approve);
        layout.Controls.Add(buttons, 0, 3);
        AcceptButton = approve;
        CancelButton = reject;
    }

    internal static RequiredOnceCreationApprovalResult ShowModal(
        RequiredOnceCreationApprovalDetails details,
        CancellationToken cancellationToken,
        Action? beforeShowForTest = null)
    {
        if (cancellationToken.IsCancellationRequested) return RequiredOnceCreationApprovalResult.HostShutdown;
        using var form = new RequiredPlanCreationApprovalForm(details);
        // Create the HWND before registering cancellation. This closes the
        // check-before-handle race; the Shown check below covers cancellation
        // while the modal loop is being entered.
        _ = form.Handle;
        form.Shown += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested && !form.IsDisposed)
                QueueClose(form);
        };
        using var registration = cancellationToken.Register(() =>
        {
            QueueClose(form);
        });
        if (cancellationToken.IsCancellationRequested) return RequiredOnceCreationApprovalResult.HostShutdown;
        beforeShowForTest?.Invoke();
        form.ShowDialog();
        return cancellationToken.IsCancellationRequested ? RequiredOnceCreationApprovalResult.HostShutdown : form._result;
    }

    private static void QueueClose(Form form)
    {
        try
        {
            if (form.IsHandleCreated && !form.IsDisposed)
                form.BeginInvoke((Action)(() => { if (!form.IsDisposed) form.Close(); }));
        }
        catch { }
    }

    private static string FormatDetails(RequiredOnceCreationApprovalDetails details)
    {
        var absolute = new AuthorizedPhysicalRectangle(
            checked(details.DisplayBounds.X + details.RegionWithinDisplay.X),
            checked(details.DisplayBounds.Y + details.RegionWithinDisplay.Y),
            details.RegionWithinDisplay.Width,
            details.RegionWithinDisplay.Height);
        return string.Join(Environment.NewLine, new[]
        {
            $"显示器 / Display: {details.DisplayName}",
            $"显示器指纹 / Display fingerprint: {details.StableDisplayFingerprint}",
            $"固定区域（物理屏幕坐标）/ Fixed region (physical virtual-screen coordinates): X={absolute.X}, Y={absolute.Y}, W={absolute.Width}, H={absolute.Height}",
            $"计划开始 / Scheduled start (UTC): {details.ScheduledStartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}",
            $"最晚允许开始 / Latest allowed start (UTC): {details.LatestStartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}",
            $"计划结束 / Planned end (UTC): {details.PlannedEndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}",
            $"时长 / Duration: {details.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds",
            "音频 / Audio: 无 / None",
            $"输出目录 / Output directory: {details.OutputDirectory}",
            $"固定文件名 / Frozen file name: {details.FrozenFileName}",
        });
    }
}
