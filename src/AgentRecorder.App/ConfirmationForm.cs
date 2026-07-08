using System;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace AgentRecorder.App;

/// <summary>
/// Non-modal confirmation form for recording requests.
/// Displays recording metadata and allows user to approve/reject.
/// Enter = Approve, Esc = Reject, Close X = Reject.
/// </summary>
internal sealed class ConfirmationForm : Form
{
    private readonly PendingConfirmationItem _item;
    private readonly int _queuePosition;
    private readonly int _totalCount;
    private readonly Action<bool>? _onResult;
    private bool _resultHandled;
    private bool _suppressCloseResult;

    public ConfirmationForm(PendingConfirmationItem item, int queuePosition, int totalCount, Action<bool>? onResult = null)
    {
        _item = item;
        _queuePosition = queuePosition;
        _totalCount = totalCount;
        _onResult = onResult;
        _resultHandled = false;
        _suppressCloseResult = false;

        SetupForm();
        BuildLayout();
    }

    private void SetupForm()
    {
        Text = "Agent Recorder — 录屏确认";
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(400, 320);

        // Handle keyboard shortcuts and close button
        KeyPreview = true;
        KeyDown += OnKeyDown;
        FormClosing += OnFormClosing;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.Handled = true;
            Approve();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Reject();
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_suppressCloseResult) return;
        if (!_resultHandled)
        {
            e.Cancel = true;
            Reject();
        }
    }

    /// <summary>
    /// Closes the form without triggering approve/reject callback.
    /// Used for programmatic close (queue advance, SetAllIdle, etc.).
    /// </summary>
    internal void CloseWithoutResult()
    {
        _suppressCloseResult = true;
        Close();
    }

    private Button _approveButton = null!;
    private Button _rejectButton = null!;

    private void BuildLayout()
    {
        var s = JsonNode.Parse(JsonSerializer.Serialize(_item.Summary))!;

        // Title label
        var titleLabel = new Label
        {
            Text = "AI 助手请求开始录屏",
            Font = new Font("Segoe UI", 12, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 20)
        };

        // Queue position
        var queueLabel = new Label
        {
            Text = $"队列位置：{_queuePosition} / {_totalCount}",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(20, 50)
        };

        // Info panel
        var infoPanel = new Panel
        {
            Location = new Point(20, 80),
            Size = new Size(360, 140),
            BackColor = Color.FromArgb(245, 245, 245),
            BorderStyle = BorderStyle.FixedSingle
        };

        int y = 10;
        AddInfoLine(infoPanel, y, "录制范围", GetString(s, "source")); y += 22;
        AddInfoLine(infoPanel, y, "时长", GetString(s, "duration")); y += 22;
        AddInfoLine(infoPanel, y, "麦克风", GetString(s, "audio")); y += 22;
        AddInfoLine(infoPanel, y, "保存位置", GetString(s, "output")); y += 22;
        AddInfoLine(infoPanel, y, "嵌套角色", GetString(s, "nested_role")); y += 22;
        AddInfoLine(infoPanel, y, "录制ID", GetString(s, "recording_id")); y += 22;
        AddInfoLine(infoPanel, y, "确认ID", GetString(s, "confirmation_id")); y += 22;
        AddInfoLine(infoPanel, y, "超时时间", GetString(s, "timeout_seconds") + "秒"); y += 22;
        AddInfoLine(infoPanel, y, "过期时间", GetString(s, "expires_at"));

        // Warning label
        var warningLabel = new Label
        {
            Text = "录屏可能包含敏感信息。只有本地确认后才会开始录制。",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.DarkRed,
            AutoSize = true,
            Location = new Point(20, 230)
        };

        // Approve button
        _approveButton = new Button
        {
            Text = "✓ 确认",
            Size = new Size(100, 35),
            Location = new Point(80, 270),
            BackColor = Color.FromArgb(0, 128, 0),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat
        };
        _approveButton.FlatAppearance.BorderColor = Color.FromArgb(0, 100, 0);
        _approveButton.Click += (_, _) => Approve();

        // Reject button
        _rejectButton = new Button
        {
            Text = "✗ 拒绝",
            Size = new Size(100, 35),
            Location = new Point(220, 270),
            BackColor = Color.FromArgb(200, 50, 50),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat
        };
        _rejectButton.FlatAppearance.BorderColor = Color.FromArgb(150, 50, 50);
        _rejectButton.Click += (_, _) => Reject();

        Controls.Add(titleLabel);
        Controls.Add(queueLabel);
        Controls.Add(infoPanel);
        Controls.Add(warningLabel);
        Controls.Add(_approveButton);
        Controls.Add(_rejectButton);

        AcceptButton = _approveButton;
        CancelButton = _rejectButton;
    }

    private void AddInfoLine(Panel panel, int y, string label, string value)
    {
        var labelLabel = new Label
        {
            Text = label + ":",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.Gray,
            AutoSize = true,
            Location = new Point(10, y)
        };

        var valueLabel = new Label
        {
            Text = value,
            Font = new Font("Segoe UI", 9),
            AutoSize = true,
            Location = new Point(100, y)
        };

        panel.Controls.Add(labelLabel);
        panel.Controls.Add(valueLabel);
    }

    private static string GetString(JsonNode node, string key)
    {
        var val = node[key];
        if (val == null) return "N/A";
        return val.ToString();
    }

    private void Approve()
    {
        if (_resultHandled) return;
        _resultHandled = true;
        _onResult?.Invoke(true);
        Close();
    }

    private void Reject()
    {
        if (_resultHandled) return;
        _resultHandled = true;
        _onResult?.Invoke(false);
        Close();
    }
}