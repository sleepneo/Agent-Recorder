using System.Drawing;
using System.Security.Principal;
using System.Windows.Forms;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

internal interface IUnattendedSafetyControlGateway
{
    StandingLeaseControlCenterQueryResult Query();
    StandingLeaseSafetyControlResult RevokeLease(string intentId, string operationId);
    StandingLeaseSafetyControlResult StopAll(string operationId);
    StandingLeaseSafetyControlResult Disable(string operationId);
    StandingLeaseSafetyControlResult Enable(string operationId);
}

internal sealed class StandingLeaseSafetyControlGateway : IUnattendedSafetyControlGateway
{
    private readonly StandingLeaseSafetyControlService _service;
    private readonly Func<string?> _currentUserSid;

    internal StandingLeaseSafetyControlGateway(
        StandingLeaseSafetyControlService service,
        Func<string?>? currentUserSidForTest = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _currentUserSid = currentUserSidForTest ?? GetCurrentUserSid;
    }

    public StandingLeaseControlCenterQueryResult Query() =>
        _service.QueryControlCenter(_currentUserSid());

    public StandingLeaseSafetyControlResult RevokeLease(string intentId, string operationId) =>
        _service.RevokeLease(intentId, operationId, "control_center_user");

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
    private readonly FlowLayoutPanel _leasePanel;
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
        MinimumSize = new Size(680, 460);
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
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 8),
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var statusHeading = new Label { AutoSize = true, Dock = DockStyle.Fill };
        var stopHeading = new Label { AutoSize = true, Dock = DockStyle.Fill };
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

        _leasePanel = new FlowLayoutPanel
        {
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8),
            Margin = new Padding(0),
        };
        _leasePanel.Resize += (_, _) => ResizeCards();
        root.Controls.Add(_leasePanel, 0, 3);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
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

    internal int LeaseCardCountForTests => _leasePanel.Controls.Count;
    internal string GlobalStatusTextForTests => _globalStatusLabel.Text;
    internal string ResultTextForTests => _resultLabel.Text;
    internal bool BusyForTests => _busy;
    internal Button StopAllButtonForTests => _stopAllButton;
    internal Button DisableButtonForTests => _disableButton;
    internal Button EnableButtonForTests => _enableButton;
    internal Button RefreshButtonForTests => _refreshButton;
    internal Button CloseButtonForTests => _closeButton;
    internal Control LeaseCardForTests(int index) => _leasePanel.Controls[index];

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
        var status = result.Status switch
        {
            StandingLeaseSafetyControlResultStatus.Changed => _text.Get("UnattendedSafety_ResultChanged"),
            StandingLeaseSafetyControlResultStatus.AlreadyApplied => _text.Get("UnattendedSafety_ResultAlreadyApplied"),
            _ => _text.Get("UnattendedSafety_ResultRejected"),
        };
        var reason = LocalizeReason(result.Reason);
        var note = operationKind == "enable" && result.Status != StandingLeaseSafetyControlResultStatus.Rejected
            ? " " + _text.Get("UnattendedSafety_EnableNote")
            : string.Empty;
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
        _leasePanel.Controls.Clear();
        foreach (var item in state.Items)
            _leasePanel.Controls.Add(CreateLeaseCard(item));
        _leasePanel.ResumeLayout();
        ResizeCards();
        _disableButton.Enabled = !_busy && state.UnattendedMode == UnattendedModeStatus.Enabled;
        _enableButton.Enabled = !_busy && state.UnattendedMode == UnattendedModeStatus.Disabled;
        _stopAllButton.Enabled = !_busy;
        _refreshButton.Enabled = !_busy;
    }

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

    private void ResizeCards()
    {
        var width = Math.Max(360, _leasePanel.ClientSize.Width - _leasePanel.Padding.Horizontal - 24);
        foreach (Control card in _leasePanel.Controls)
        {
            card.Width = width;
            if (card.Controls.Count > 0 && card.Controls[0] is TableLayoutPanel content && content.Controls.Count > 0)
            {
                content.Controls[0].MaximumSize = new Size(Math.Max(220, width - 150), 0);
            }
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
