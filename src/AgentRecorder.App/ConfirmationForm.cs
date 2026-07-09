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
    private readonly IScreenPreviewProvider _previewProvider;
    private bool _resultHandled;
    private bool _suppressCloseResult;

    private Button _approveButton = null!;
    private Button _rejectButton = null!;
    private PictureBox _previewBox = null!;
    private Label _previewFallbackLabel = null!;
    private Label _previewBoundsLabel = null!;
    private Panel _previewPanel = null!;

    internal bool HasPreviewAreaForTests => _previewPanel != null;
    internal bool HasPreviewImageForTests => _previewBox?.Image != null;
    internal string PreviewBoundsTextForTests => _previewBoundsLabel?.Text ?? "";
    internal string PreviewFallbackTextForTests => _previewFallbackLabel?.Text ?? "";

    public ConfirmationForm(PendingConfirmationItem item, int queuePosition, int totalCount, Action<bool>? onResult = null, IScreenPreviewProvider? previewProvider = null)
    {
        _item = item;
        _queuePosition = queuePosition;
        _totalCount = totalCount;
        _onResult = onResult;
        _previewProvider = previewProvider ?? new GdiScreenPreviewProvider();
        _resultHandled = false;
        _suppressCloseResult = false;

        SetupForm();
        BuildLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewBox?.Image?.Dispose();
            if (_previewBox != null)
                _previewBox.Image = null;
        }
        base.Dispose(disposing);
    }

    private void SetupForm()
    {
        Text = "Agent Recorder — 录屏确认";
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(620, 500);

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

        // Info panel (left)
        var infoPanel = new Panel
        {
            Location = new Point(20, 80),
            Size = new Size(300, 240),
            BackColor = Color.FromArgb(245, 245, 245),
            BorderStyle = BorderStyle.FixedSingle
        };

        int y = 10;
        AddInfoLine(infoPanel, y, "录制范围", GetString(s, "source")); y += 20;
        AddInfoLine(infoPanel, y, "来源类型", GetString(s, "source_type")); y += 20;
        AddInfoLine(infoPanel, y, "来源标题", GetString(s, "source_title")); y += 20;
        AddInfoLine(infoPanel, y, "时长", GetString(s, "duration")); y += 20;
        AddInfoLine(infoPanel, y, "麦克风", GetString(s, "audio")); y += 20;
        AddInfoLine(infoPanel, y, "保存位置", GetString(s, "output")); y += 20;
        AddInfoLine(infoPanel, y, "嵌套角色", GetString(s, "nested_role")); y += 20;
        AddInfoLine(infoPanel, y, "录制ID", GetString(s, "recording_id")); y += 20;
        AddInfoLine(infoPanel, y, "确认ID", GetString(s, "confirmation_id")); y += 20;
        AddInfoLine(infoPanel, y, "超时时间", GetString(s, "timeout_seconds") + "秒"); y += 20;
        AddInfoLine(infoPanel, y, "过期时间", GetString(s, "expires_at"));

        // Preview panel (right)
        _previewPanel = new Panel
        {
            Location = new Point(340, 80),
            Size = new Size(260, 220),
            BackColor = Color.FromArgb(32, 32, 32),
            BorderStyle = BorderStyle.FixedSingle
        };

        _previewBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            Visible = false
        };

        _previewFallbackLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
            Visible = false
        };

        _previewPanel.Controls.Add(_previewBox);
        _previewPanel.Controls.Add(_previewFallbackLabel);

        _previewBoundsLabel = new Label
        {
            Location = new Point(340, 310),
            Size = new Size(260, 20),
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false
        };

        var bounds = ConfirmationPreviewBuilder.ParseBounds(s);
        _previewBoundsLabel.Text = bounds != null
            ? $"X={bounds.X} Y={bounds.Y} W={bounds.Width} H={bounds.Height}"
            : "未提供录制范围";

        var previewBitmap = ConfirmationPreviewBuilder.TryBuildPreview(s, _previewProvider, new Size(260, 180), out var fallbackMessage);
        if (previewBitmap != null)
        {
            _previewBox.Image = previewBitmap;
            _previewBox.Visible = true;
            _previewFallbackLabel.Visible = false;
        }
        else
        {
            _previewBox.Visible = false;
            _previewFallbackLabel.Text = fallbackMessage;
            _previewFallbackLabel.Visible = true;
        }

        // Warning label
        var warningLabel = new Label
        {
            Text = "录屏可能包含敏感信息。只有本地确认后才会开始录制。",
            Font = new Font("Segoe UI", 9),
            ForeColor = Color.DarkRed,
            AutoSize = true,
            Location = new Point(20, 340)
        };

        // Approve button
        _approveButton = new Button
        {
            Text = "✓ 确认",
            Size = new Size(120, 40),
            Location = new Point(140, 420),
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
            Size = new Size(120, 40),
            Location = new Point(320, 420),
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
        Controls.Add(_previewPanel);
        Controls.Add(_previewBoundsLabel);
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