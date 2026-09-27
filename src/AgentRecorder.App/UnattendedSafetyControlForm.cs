using System.Drawing;
using System.Security.Principal;
using System.Windows.Forms;
using AgentRecorder.Capture;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

internal interface IUnattendedSafetyControlGateway
{
    StandingLeaseControlCenterQueryResult Query();
    StandingLeaseSafetyControlResult RevokeLease(string intentId, string operationId);
    StandingLeaseSafetyControlResult RevokeRecurringLease(string leaseId, string operationId);
    StandingLeaseSafetyControlResult StopAll(string operationId);
    StandingLeaseSafetyControlResult Disable(string operationId);
    StandingLeaseSafetyControlResult Enable(string operationId);
}

internal sealed class StandingLeaseSafetyControlGateway : IUnattendedSafetyControlGateway
{
    private readonly StandingLeaseSafetyControlService _service;
    private readonly Func<string?> _currentUserSid;
    private readonly Func<string?> _currentSessionBinding;

    internal StandingLeaseSafetyControlGateway(
        StandingLeaseSafetyControlService service,
        Func<string?>? currentUserSidForTest = null,
        Func<string?>? currentSessionBindingForTest = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _currentUserSid = currentUserSidForTest ?? GetCurrentUserSid;
        _currentSessionBinding = currentSessionBindingForTest ?? (() => CaptureAuthorizationSessionBinding.Current);
    }

    public StandingLeaseControlCenterQueryResult Query() =>
        _service.QueryControlCenter(_currentUserSid(), _currentSessionBinding());

    public StandingLeaseSafetyControlResult RevokeLease(string intentId, string operationId) =>
        _service.RevokeLease(intentId, operationId, "control_center_user");

    public StandingLeaseSafetyControlResult RevokeRecurringLease(string leaseId, string operationId) =>
        _service.RevokeRecurringLease(leaseId, operationId, "control_center_user");

    public StandingLeaseSafetyControlResult StopAll(string operationId) =>
        _service.StopAllAndRevokeAll(operationId, "control_center_user");

    public StandingLeaseSafetyControlResult Disable(string operationId) =>
        _service.DisableUnattended(operationId, "control_center_user");

    public StandingLeaseSafetyControlResult Enable(string operationId) =>
        _service.EnableUnattended(operationId, "control_center_user");

    private static string? GetCurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value;
}

internal interface IUnattendedSafetyConfirmation
{
    bool Confirm(IWin32Window owner, string title, string message);
}

internal sealed class MessageBoxUnattendedSafetyConfirmation : IUnattendedSafetyConfirmation
{
    public bool Confirm(IWin32Window owner, string title, string message) =>
        MessageBox.Show(
            owner,
            message,
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
}

internal sealed class UnattendedSafetyControlForm : Form
{
    private readonly IUnattendedSafetyControlGateway _gateway;
    private readonly IUnattendedSafetyConfirmation _confirmation;
    private IUiTextProvider _text;
    private readonly Label _globalStatusLabel;
    private readonly Label _stopSummaryLabel;
    private readonly Label _resultLabel;
    private readonly Button _refreshButton;
    private readonly Button _stopAllButton;
    private readonly Button _disableButton;
    private readonly Button _enableButton;
    private readonly Button _closeButton;
    private readonly TableLayoutPanel _leaseLists;
    private readonly FlowLayoutPanel _leasePanel;
    private readonly FlowLayoutPanel _recurringPanel;
    private bool _busy;
    private bool _allowClose;

    internal UnattendedSafetyControlForm(
        IUnattendedSafetyControlGateway gateway,
        IUiTextProvider textProvider,
        IUnattendedSafetyConfirmation? confirmation = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _text = textProvider ?? throw new ArgumentNullException(nameof(textProvider));
        _confirmation = confirmation ?? new MessageBoxUnattendedSafetyConfirmation();

        Text = _text.Get("UnattendedSafety_Title");
        Name = "UnattendedSafetyControlForm";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(680, 520);
        ClientSize = new Size(900, 660);
        KeyPreview = true;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
            BackColor = SystemColors.Window,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
        };
        root.Controls.Add(title, 0, 0);

        var statusPanel = new TableLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8),
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var statusHeading = new Label { AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left };
        var stopHeading = new Label { AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left };
        _globalStatusLabel = new Label { AutoSize = true, Dock = DockStyle.Fill };
        _stopSummaryLabel = new Label { AutoSize = true, Dock = DockStyle.Fill };
        statusPanel.Controls.Add(statusHeading, 0, 0);
        statusPanel.Controls.Add(_globalStatusLabel, 1, 0);
        statusPanel.Controls.Add(stopHeading, 0, 1);
        statusPanel.Controls.Add(_stopSummaryLabel, 1, 1);
        root.Controls.Add(statusPanel, 0, 1);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        _refreshButton = CreateButton(actions);
        _stopAllButton = CreateButton(actions);
        _disableButton = CreateButton(actions);
        _enableButton = CreateButton(actions);
        root.Controls.Add(actions, 0, 2);

        _leaseLists = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        var oneShotGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Text = _text.Get("UnattendedSafety_OneShotHeading"),
            Tag = "one_shot_heading",
        };
        var recurringGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            Text = _text.Get("UnattendedSafety_RecurringHeading"),
            Tag = "recurring_heading",
        };
        _leasePanel = CreateLeaseListPanel();
        _recurringPanel = CreateLeaseListPanel();
        oneShotGroup.Controls.Add(_leasePanel);
        recurringGroup.Controls.Add(_recurringPanel);
        _leaseLists.Controls.Add(oneShotGroup, 0, 0);
        _leaseLists.Controls.Add(recurringGroup, 0, 1);
        SetLeaseSectionRows(oneShotCount: 0, recurringCount: 0);
        _leasePanel.Resize += (_, _) => ResizeCards();
        _recurringPanel.Resize += (_, _) => ResizeCards();
        root.Controls.Add(_leaseLists, 0, 3);

        var footer = new TableLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0, 8, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _resultLabel = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DarkRed };
        _closeButton = CreateButton(null);
        footer.Controls.Add(_resultLabel, 0, 0);
        footer.Controls.Add(_closeButton, 1, 0);
        root.Controls.Add(footer, 0, 4);

        title.Tag = "title";
        statusHeading.Tag = "status_heading";
        stopHeading.Tag = "stop_heading";
        _globalStatusLabel.Tag = "global_status_value";
        _stopSummaryLabel.Tag = "stop_summary_value";
        _resultLabel.Tag = "result";
        _refreshButton.Tag = "refresh";
        _stopAllButton.Tag = "stop_all";
        _disableButton.Tag = "disable";
        _enableButton.Tag = "enable";
        _closeButton.Tag = "close";

        _refreshButton.Click += (_, _) => RefreshState();
        _stopAllButton.Click += (_, _) => ConfirmAndStopAll();
        _disableButton.Click += (_, _) => ConfirmAndDisable();
        _enableButton.Click += (_, _) => ExecuteControl(
            "enable",
            operationId => _gateway.Enable(operationId));
        _closeButton.Click += (_, _) => Hide();
        AcceptButton = _refreshButton;
        CancelButton = _closeButton;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Hide();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        ApplyStaticText();
        RefreshState();
    }

    internal int LeaseCardCountForTests => _leasePanel.Controls.Cast<Control>().Count(IsLeaseCard);
    internal int RecurringLeaseCardCountForTests => _recurringPanel.Controls.Cast<Control>().Count(IsLeaseCard);
    internal TableLayoutPanel LeaseListsForTests => _leaseLists;
    internal string GlobalStatusTextForTests => _globalStatusLabel.Text;
    internal string ResultTextForTests => _resultLabel.Text;
    internal bool BusyForTests => _busy;
    internal Button StopAllButtonForTests => _stopAllButton;
    internal Button DisableButtonForTests => _disableButton;
    internal Button EnableButtonForTests => _enableButton;
    internal Button RefreshButtonForTests => _refreshButton;
    internal Button CloseButtonForTests => _closeButton;
    internal Control LeaseCardForTests(int index) => _leasePanel.Controls[index];
    internal Control RecurringLeaseCardForTests(int index) => _recurringPanel.Controls[index];

    internal void RefreshFromTray() => RefreshState();

    internal void UpdateLanguage(IUiTextProvider textProvider)
    {
        _text = textProvider ?? throw new ArgumentNullException(nameof(textProvider));
        ApplyStaticText();
        RefreshState();
    }

    internal void CloseForOwner()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private Button CreateButton(Control? parent)
    {
        var button = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 8, 0),
            TabStop = true,
        };
        parent?.Controls.Add(button);

        return button;
    }

    private void ApplyStaticText()
    {
        Text = _text.Get("UnattendedSafety_Title");
        if (Controls.Count > 0)
            ApplyStaticText(Controls[0]);
    }

    private void ApplyStaticText(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control.Tag is string tag)
            {
                control.Text = tag switch
                {
                    "title" => _text.Get("UnattendedSafety_Heading"),
                    "status_heading" => _text.Get("UnattendedSafety_GlobalStatus"),
                    "stop_heading" => _text.Get("UnattendedSafety_LastStopAll"),
                    "refresh" => _text.Get("UnattendedSafety_Refresh"),
                    "stop_all" => _text.Get("UnattendedSafety_StopAll"),
                    "disable" => _text.Get("UnattendedSafety_Disable"),
                    "enable" => _text.Get("UnattendedSafety_Enable"),
                    "close" => _text.Get("UnattendedSafety_Close"),
                    "one_shot_heading" => _text.Get("UnattendedSafety_OneShotHeading"),
                    "recurring_heading" => _text.Get("UnattendedSafety_RecurringHeading"),
                    _ => control.Text,
                };
            }

            if (control.Controls.Count > 0)
                ApplyStaticText(control);
        }
    }

    private void ConfirmAndStopAll()
    {
        if (!_confirmation.Confirm(
                this,
                _text.Get("UnattendedSafety_ConfirmTitle"),
                _text.Get("UnattendedSafety_ConfirmStopAll")))
        {
            return;
        }

        ExecuteControl("stop_all", operationId => _gateway.StopAll(operationId));
    }

    private void ConfirmAndDisable()
    {
        if (!_confirmation.Confirm(
                this,
                _text.Get("UnattendedSafety_ConfirmTitle"),
                _text.Get("UnattendedSafety_ConfirmDisable")))
        {
            return;
        }

        ExecuteControl("disable", operationId => _gateway.Disable(operationId));
    }

    private void ExecuteControl(
        string operationKind,
        Func<string, StandingLeaseSafetyControlResult> operation)
    {
        if (_busy)
            return;

        SetBusy(true);
        StandingLeaseSafetyControlResult result;
        var operationId = "control-center-" + operationKind + "-" + Guid.NewGuid().ToString("N");
        try
        {
            result = operation(operationId);
            _resultLabel.ForeColor = result.Status == StandingLeaseSafetyControlResultStatus.Rejected
                ? Color.DarkRed
                : Color.DarkGreen;
            _resultLabel.Text = FormatControlResult(operationKind, result);
        }
        catch (Exception exception)
        {
            _resultLabel.ForeColor = Color.DarkRed;
            _resultLabel.Text = _text.Format(
                "UnattendedSafety_Failure",
                _text.Get("UnattendedSafety_Reason_OperationFailed"),
                exception.GetType().Name);
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private string FormatControlResult(string operationKind, StandingLeaseSafetyControlResult result)
    {
        var status = operationKind == "recurring-revoke" &&
            result.DurableStateChanged &&
            result.Status != StandingLeaseSafetyControlResultStatus.AlreadyApplied
            ? _text.Get("UnattendedSafety_ResultRecurringRevoked")
            : result.Status switch
            {
                StandingLeaseSafetyControlResultStatus.Changed => _text.Get("UnattendedSafety_ResultChanged"),
                StandingLeaseSafetyControlResultStatus.AlreadyApplied => _text.Get("UnattendedSafety_ResultAlreadyApplied"),
                _ => _text.Get("UnattendedSafety_ResultRejected"),
            };
        var reason = LocalizeReason(result.Reason);
        var note = operationKind == "enable" && result.Status != StandingLeaseSafetyControlResultStatus.Rejected
            ? " " + _text.Get("UnattendedSafety_EnableNote")
            : string.Empty;
        if (operationKind == "recurring-revoke" &&
            result.RequiresActiveRunStop &&
            result.DurableOperationCommitted &&
            (result.Status != StandingLeaseSafetyControlResultStatus.Rejected || result.DurableStateChanged))
        {
            note += " " + _text.Get(result.PhysicalStopFailed
                ? "UnattendedSafety_RecurringStopFailed"
                : result.PhysicalStopNoOp
                    ? "UnattendedSafety_RecurringStopNoOp"
                    : "UnattendedSafety_RecurringStopRequested");
        }
        return _text.Format("UnattendedSafety_Result", status, reason, result.Reason) + note;
    }

    private void RefreshState()
    {
        if (_busy)
            return;

        StandingLeaseControlCenterQueryResult result;
        try
        {
            result = _gateway.Query();
        }
        catch (Exception exception)
        {
            result = StandingLeaseControlCenterQueryResult.Rejected(
                exception is InvalidOperationException
                    ? "safety_user_identity_invalid"
                    : "safety_query_failed");
        }

        if (result.Status != StandingLeaseControlCenterQueryStatus.Available || result.State is null)
        {
            _globalStatusLabel.Text = _text.Get("UnattendedSafety_StatusUnavailable");
            _stopSummaryLabel.Text = _text.Format("UnattendedSafety_ReasonCode", LocalizeReason(result.Reason), result.Reason);
            _resultLabel.Text = string.Empty;
            _leasePanel.Controls.Clear();
            _recurringPanel.Controls.Clear();
            SetEmptyState(_leasePanel, "UnattendedSafety_CategoryUnavailable", "empty_state_one_shot");
            SetEmptyState(_recurringPanel, "UnattendedSafety_CategoryUnavailable", "empty_state_recurring");
            SetLeaseSectionRows(oneShotCount: 0, recurringCount: 0);
            _stopAllButton.Enabled = false;
            _disableButton.Enabled = false;
            _enableButton.Enabled = false;
            _refreshButton.Enabled = !_busy;
            return;
        }

        var state = result.State;
        _globalStatusLabel.Text = state.UnattendedMode == UnattendedModeStatus.Enabled
            ? _text.Get("UnattendedSafety_Enabled")
            : _text.Get("UnattendedSafety_Disabled");
        _stopSummaryLabel.Text = state.StopAllApplied
            ? _text.Format(
                "UnattendedSafety_StopSummary",
                FormatDate(state.StopAllAppliedAtUtc),
                LocalizeReason(state.StopAllReasonCode),
                state.StopAllReasonCode ?? _text.Get("UnattendedSafety_NotAvailable"))
            : _text.Get("UnattendedSafety_NoStopAll");

        _leasePanel.SuspendLayout();
        _recurringPanel.SuspendLayout();
        _leasePanel.Controls.Clear();
        _recurringPanel.Controls.Clear();
        foreach (var item in state.Items)
            _leasePanel.Controls.Add(CreateLeaseCard(item));
        foreach (var item in state.RecurringItems)
            _recurringPanel.Controls.Add(CreateRecurringLeaseCard(item));
        if (state.Items.Count == 0)
            SetEmptyState(_leasePanel, "UnattendedSafety_EmptyOneShot", "empty_state_one_shot");
        if (state.RecurringItems.Count == 0)
            SetEmptyState(_recurringPanel, "UnattendedSafety_EmptyRecurring", "empty_state_recurring");
        SetLeaseSectionRows(state.Items.Count, state.RecurringItems.Count);
        _leasePanel.ResumeLayout();
        _recurringPanel.ResumeLayout();
        ResizeCards();
        _disableButton.Enabled = !_busy && state.UnattendedMode == UnattendedModeStatus.Enabled;
        _enableButton.Enabled = !_busy && state.UnattendedMode == UnattendedModeStatus.Disabled;
        _stopAllButton.Enabled = !_busy;
        _refreshButton.Enabled = !_busy;
    }

    private static FlowLayoutPanel CreateLeaseListPanel() => new()
    {
        AutoScroll = true,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Padding = new Padding(8),
        Margin = new Padding(0),
    };

    private void SetEmptyState(FlowLayoutPanel panel, string textKey, string tag)
    {
        var emptyState = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 2),
            Text = _text.Get(textKey),
            Tag = tag,
        };
        panel.Controls.Add(emptyState);
    }

    private void SetLeaseSectionRows(int oneShotCount, int recurringCount)
    {
        _leaseLists.RowStyles.Clear();
        if (oneShotCount == 0 && recurringCount == 0)
        {
            _leaseLists.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactEmptySectionHeight(_leasePanel, 0)));
            _leaseLists.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactEmptySectionHeight(_recurringPanel, 1)));
        }
        else if (oneShotCount == 0)
        {
            _leaseLists.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactEmptySectionHeight(_leasePanel, 0)));
            _leaseLists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        }
        else if (recurringCount == 0)
        {
            _leaseLists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _leaseLists.RowStyles.Add(new RowStyle(SizeType.Absolute, CompactEmptySectionHeight(_recurringPanel, 1)));
        }
        else
        {
            _leaseLists.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _leaseLists.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        }
    }

    private float CompactEmptySectionHeight(FlowLayoutPanel list, int row)
    {
        var group = _leaseLists.GetControlFromPosition(0, row) as GroupBox;
        var label = list.Controls
            .OfType<Label>()
            .FirstOrDefault(control => control.Tag is string tag && tag.StartsWith("empty_state_", StringComparison.Ordinal));
        var labelHeight = label?.PreferredSize.Height ?? Font.Height;
        var groupFontHeight = group?.Font.Height ?? Font.Height;
        return Math.Max(64, labelHeight + list.Padding.Vertical + groupFontHeight + 21);
    }

    private static bool IsLeaseCard(Control control) =>
        !string.Equals(control.Tag as string, "empty_state_one_shot", StringComparison.Ordinal) &&
        !string.Equals(control.Tag as string, "empty_state_recurring", StringComparison.Ordinal);

    private Control CreateLeaseCard(StandingLeaseControlCenterLeaseSummary item)
    {
        var card = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
            Tag = item.IntentId,
        };
        var content = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var details = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(760, 0),
            Text = FormatItem(item),
            Margin = new Padding(0, 0, 10, 0),
        };
        var revoke = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = _text.Get("UnattendedSafety_Revoke"),
            AccessibleName = _text.Get("UnattendedSafety_Revoke"),
            Tag = item.IntentId,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Enabled = item.LeaseStatus is ConsentLeaseStatus.Pending or ConsentLeaseStatus.Active,
        };
        revoke.Click += (_, _) => ConfirmAndRevoke(item);
        content.Controls.Add(details, 0, 0);
        content.SetRowSpan(details, 5);
        content.Controls.Add(revoke, 1, 0);
        card.Controls.Add(content);
        return card;
    }

    private void ConfirmAndRevoke(StandingLeaseControlCenterLeaseSummary item)
    {
        if (!_confirmation.Confirm(
                this,
                _text.Get("UnattendedSafety_ConfirmTitle"),
                _text.Format("UnattendedSafety_ConfirmRevoke", item.IntentId)))
        {
            return;
        }

        ExecuteControl(
            "revoke",
            operationId => _gateway.RevokeLease(item.IntentId, operationId));
    }

    private Control CreateRecurringLeaseCard(StandingLeaseControlCenterRecurringLeaseSummary item)
    {
        var card = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 0, 8),
            Tag = item.LeaseId,
        };
        var content = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var details = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size(760, 0),
            Text = FormatRecurringItem(item),
            Margin = new Padding(0, 0, 10, 0),
        };
        var revoke = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Text = _text.Get("UnattendedSafety_Revoke"),
            AccessibleName = _text.Get("UnattendedSafety_Revoke"),
            Tag = item.LeaseId,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Enabled = item.CanRevoke,
        };
        revoke.Click += (_, _) => ConfirmAndRevokeRecurring(item);
        content.Controls.Add(details, 0, 0);
        content.Controls.Add(revoke, 1, 0);
        card.Controls.Add(content);
        return card;
    }

    private void ConfirmAndRevokeRecurring(StandingLeaseControlCenterRecurringLeaseSummary item)
    {
        if (_busy)
            return;

        StandingLeaseControlCenterQueryResult liveQuery;
        try
        {
            liveQuery = _gateway.Query();
        }
        catch
        {
            liveQuery = StandingLeaseControlCenterQueryResult.Rejected("safety_query_failed");
        }

        var liveMatches = liveQuery.State?.RecurringItems
            .Where(candidate => string.Equals(candidate.LeaseId, item.LeaseId, StringComparison.Ordinal))
            .ToArray() ?? Array.Empty<StandingLeaseControlCenterRecurringLeaseSummary>();
        if (liveQuery.Status != StandingLeaseControlCenterQueryStatus.Available ||
            liveMatches.Length != 1 ||
            !string.Equals(liveMatches[0].PlanId, item.PlanId, StringComparison.Ordinal) ||
            !liveMatches[0].CanRevoke)
        {
            RefreshState();
            _resultLabel.ForeColor = Color.DarkRed;
            _resultLabel.Text = _text.Get("UnattendedSafety_RecurringStale");
            return;
        }

        var liveItem = liveMatches[0];
        if (!_confirmation.Confirm(
                this,
                _text.Get("UnattendedSafety_ConfirmTitle"),
                _text.Format(
                    "UnattendedSafety_ConfirmRecurringRevoke",
                    liveItem.PlanId,
                    liveItem.LeaseId,
                    liveItem.ActiveRunPresent ? _text.Get("UnattendedSafety_Yes") : _text.Get("UnattendedSafety_No"))))
        {
            return;
        }

        ExecuteControl(
            "recurring-revoke",
            operationId => _gateway.RevokeRecurringLease(liveItem.LeaseId, operationId));
    }

    private string FormatItem(StandingLeaseControlCenterLeaseSummary item)
    {
        var leaseStatus = item.LeaseStatus is null
            ? _text.Get("UnattendedSafety_NotBound")
            : LocalizeStatus(item.LeaseStatus.Value.ToString());
        var occurrenceStatus = item.OccurrenceStatus is null
            ? _text.Get("UnattendedSafety_NotAvailable")
            : LocalizeStatus(item.OccurrenceStatus.Value.ToString());
        var reason = item.BlockedReason is null
            ? _text.Get("UnattendedSafety_NoBlockReason")
            : _text.Format(
                "UnattendedSafety_ReasonCode",
                LocalizeReason(item.BlockedReason),
                item.BlockedReason);
        return _text.Format(
            "UnattendedSafety_Item",
            item.IntentId,
            LocalizeStatus(item.IntentStatus.ToString()),
            item.PlanId ?? _text.Get("UnattendedSafety_NotAvailable"),
            item.OccurrenceId ?? _text.Get("UnattendedSafety_NotAvailable"),
            item.LeaseId ?? _text.Get("UnattendedSafety_NotAvailable"),
            leaseStatus,
            occurrenceStatus,
            FormatDate(item.LeaseValidUntilUtc),
            FormatDate(item.ScheduledStartUtc),
            FormatDate(item.LatestStartUtc),
            FormatDate(item.PlannedEndUtc),
            item.MaximumDuration,
            item.ActiveRunPresent ? _text.Get("UnattendedSafety_Yes") : _text.Get("UnattendedSafety_No"),
            reason);
    }

    private string FormatRecurringItem(StandingLeaseControlCenterRecurringLeaseSummary item)
    {
        var scheduleKind = item.ScheduleKind == RecurringScheduleKind.Daily
            ? _text.Get("UnattendedSafety_Daily")
            : _text.Get("UnattendedSafety_Weekly");
        var weekdays = item.ScheduleKind == RecurringScheduleKind.Weekly
            ? string.Join(", ", item.WeeklyDays.Select(FormatWeekday))
            : _text.Get("UnattendedSafety_NotApplicable");
        var nextOccurrence = item.NextOccurrenceLocalDate is { } date && item.NextOccurrenceLocalTime is { } time
            ? date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " " +
              time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " (" + item.TimeZoneId + ")"
            : _text.Get("UnattendedSafety_NoFurtherOccurrence");
        return _text.Format(
            "UnattendedSafety_RecurringItem",
            item.PlanId,
            item.LeaseId,
            scheduleKind,
            item.TimeZoneId,
            item.LocalStartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            item.LocalEndDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            item.LocalWallClockTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            weekdays,
            nextOccurrence,
            LocalizeStatus(item.LeaseStatus.ToString()),
            FormatDate(item.LeaseValidUntilUtc),
            item.RemainingUses,
            item.RemainingDuration.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            item.ActiveRunPresent ? _text.Get("UnattendedSafety_Yes") : _text.Get("UnattendedSafety_No"));
    }

    private string FormatWeekday(DayOfWeek weekday) => _text.Get(weekday switch
    {
        DayOfWeek.Monday => "UnattendedSafety_DayMonday",
        DayOfWeek.Tuesday => "UnattendedSafety_DayTuesday",
        DayOfWeek.Wednesday => "UnattendedSafety_DayWednesday",
        DayOfWeek.Thursday => "UnattendedSafety_DayThursday",
        DayOfWeek.Friday => "UnattendedSafety_DayFriday",
        DayOfWeek.Saturday => "UnattendedSafety_DaySaturday",
        _ => "UnattendedSafety_DaySunday",
    });

    private void ResizeCards()
    {
        ResizeCards(_leasePanel);
        ResizeCards(_recurringPanel);
    }

    private static void ResizeCards(FlowLayoutPanel list)
    {
        var scrollBarWidth = SystemInformation.VerticalScrollBarWidth;
        var availableWidth = Math.Max(
            100,
            list.ClientSize.Width - list.Padding.Horizontal - scrollBarWidth - 4);
        foreach (Control card in list.Controls)
        {
            if (card is not Panel || card.Tag is not string tag || tag.StartsWith("empty_state_", StringComparison.Ordinal))
                continue;

            card.MinimumSize = new Size(availableWidth, 0);
            card.Width = availableWidth;
            if (card.Controls.Count == 0 || card.Controls[0] is not TableLayoutPanel content || content.Controls.Count < 2)
                continue;

            var details = content.Controls[0];
            var action = content.Controls[1];
            var reservedWidth = action.PreferredSize.Width + action.Margin.Horizontal + details.Margin.Horizontal +
                content.Padding.Horizontal + card.Padding.Horizontal + 14;
            details.MaximumSize = new Size(Math.Max(100, availableWidth - reservedWidth), 0);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _refreshButton.Enabled = !busy;
        _stopAllButton.Enabled = !busy;
        _disableButton.Enabled = !busy;
        _enableButton.Enabled = !busy;
        foreach (Control card in _leasePanel.Controls)
        {
            foreach (Control child in card.Controls)
                SetChildButtonsEnabled(child, !busy);
        }
        foreach (Control card in _recurringPanel.Controls)
        {
            foreach (Control child in card.Controls)
                SetChildButtonsEnabled(child, !busy);
        }
    }

    private static void SetChildButtonsEnabled(Control control, bool enabled)
    {
        if (control is Button button)
            button.Enabled &= enabled;
        foreach (Control child in control.Controls)
            SetChildButtonsEnabled(child, enabled);
    }

    private string FormatDate(DateTimeOffset? value) =>
        value is null
            ? _text.Get("UnattendedSafety_NotAvailable")
            : value.Value.ToLocalTime().ToString("g");

    private string LocalizeStatus(string value) => value switch
    {
        nameof(UnattendedModeStatus.Enabled) => _text.Get("UnattendedSafety_Enabled"),
        nameof(UnattendedModeStatus.Disabled) => _text.Get("UnattendedSafety_Disabled"),
        nameof(StandingSetupIntentStatus.RegionSelectionPending) => _text.Get("UnattendedSafety_StatusRegionPending"),
        nameof(StandingSetupIntentStatus.LeaseApprovalPending) => _text.Get("UnattendedSafety_StatusApprovalPending"),
        nameof(StandingSetupIntentStatus.Activated) => _text.Get("UnattendedSafety_StatusActivated"),
        nameof(StandingSetupIntentStatus.Rejected) => _text.Get("UnattendedSafety_StatusRejected"),
        nameof(StandingSetupIntentStatus.Expired) => _text.Get("UnattendedSafety_StatusExpired"),
        nameof(ConsentLeaseStatus.Pending) => _text.Get("UnattendedSafety_StatusPending"),
        nameof(ConsentLeaseStatus.Active) => _text.Get("UnattendedSafety_StatusActive"),
        nameof(ConsentLeaseStatus.Revoked) => _text.Get("UnattendedSafety_StatusRevoked"),
        nameof(ConsentLeaseStatus.Exhausted) => _text.Get("UnattendedSafety_StatusExhausted"),
        _ => value,
    };

    private string LocalizeReason(string? reason) => reason switch
    {
        StandingLeaseSafetyReasonCodes.UnattendedDisabled => _text.Get("UnattendedSafety_Reason_UnattendedDisabled"),
        StandingLeaseSafetyReasonCodes.LeaseRevoked => _text.Get("UnattendedSafety_Reason_LeaseRevoked"),
        StandingLeaseSafetyReasonCodes.ReenableRequiresNewAuthorization => _text.Get("UnattendedSafety_Reason_Reenable"),
        StandingLeaseSafetyReasonCodes.LeasePending => _text.Get("UnattendedSafety_Reason_LeasePending"),
        StandingLeaseSafetyReasonCodes.LeaseExpired => _text.Get("UnattendedSafety_Reason_LeaseExpired"),
        StandingLeaseSafetyReasonCodes.LeaseExhausted => _text.Get("UnattendedSafety_Reason_LeaseExhausted"),
        StandingLeaseSafetyReasonCodes.LeaseRejected => _text.Get("UnattendedSafety_Reason_LeaseRejected"),
        "region_selection_pending" => _text.Get("UnattendedSafety_Reason_RegionPending"),
        "lease_approval_pending" => _text.Get("UnattendedSafety_Reason_ApprovalPending"),
        "occurrence_window_expired" => _text.Get("UnattendedSafety_Reason_WindowExpired"),
        "safety_intent_not_bound" => _text.Get("UnattendedSafety_Reason_IntentNotBound"),
        "safety_snapshot_invalid" => _text.Get("UnattendedSafety_Reason_SnapshotInvalid"),
        "safety_sqlite_failure" => _text.Get("UnattendedSafety_Reason_SqliteFailure"),
        "safety_query_failed" => _text.Get("UnattendedSafety_Reason_QueryFailed"),
        "safety_user_identity_invalid" => _text.Get("UnattendedSafety_Reason_UserIdentityInvalid"),
        "stop_all_applied" => _text.Get("UnattendedSafety_Reason_StopAllApplied"),
        "unattended_enabled" => _text.Get("UnattendedSafety_Reason_UnattendedEnabled"),
        StandingLeaseSafetyReasonCodes.ActiveRunStopFailed => _text.Get("UnattendedSafety_Reason_ActiveRunStopFailed"),
        "available" => _text.Get("UnattendedSafety_Reason_Available"),
        null or "" => _text.Get("UnattendedSafety_NotAvailable"),
        _ => reason,
    };
}
