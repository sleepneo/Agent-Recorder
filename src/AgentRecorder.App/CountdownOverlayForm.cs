using System;
using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Top-most, click-through, non-activating overlay that shows a single large
/// countdown digit in the center of the capture region. Excluded from capture
/// so it does not appear in the final recording.
/// </summary>
internal sealed class CountdownOverlayForm : Form
{
    private readonly Label _label;
    private readonly IWindowDisplayAffinity _displayAffinity;
    private bool _displayAffinityApplied;
    private Exception? _displayAffinityError;

    internal bool DisplayAffinityAppliedForTests => _displayAffinityApplied;
    internal Exception? DisplayAffinityErrorForTests => _displayAffinityError;
    internal string LabelTextForTests => _label.Text;

    public CountdownOverlayForm(Rectangle bounds, IWindowDisplayAffinity? displayAffinity = null)
    {
        _displayAffinity = displayAffinity ?? WindowDisplayAffinity.Instance;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
        Opacity = 1.0;
        DoubleBuffered = true;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        Text = "";
        AutoScaleMode = AutoScaleMode.Dpi;

        Bounds = bounds;

        _label = new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(255, 255, 165, 0),
            Font = new Font("Segoe UI", 72, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Text = "3"
        };
        Controls.Add(_label);
    }

    public void SetNumber(int value)
    {
        _label.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x80000;
            const int WS_EX_TRANSPARENT = 0x20;
            const int WS_EX_NOACTIVATE = 0x8000000;
            const int WS_EX_TOOLWINDOW = 0x80;

            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            cp.Style &= ~(0x00C00000 | 0x00040000 | 0x00010000);
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDisplayAffinity(Handle);
    }

    private void ApplyDisplayAffinity(IntPtr hWnd)
    {
        _displayAffinityApplied = false;
        _displayAffinityError = null;

        try
        {
            _displayAffinityApplied = _displayAffinity.SetExcludeFromCapture(hWnd);
        }
        catch (Exception ex)
        {
            _displayAffinityError = ex;
            _displayAffinityApplied = false;
        }
    }

    /// <summary>
    /// Closes the overlay without side effects. Safe to call multiple times.
    /// </summary>
    internal void CloseWithoutResult()
    {
        try { Close(); } catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _label?.Font?.Dispose();
        }
        base.Dispose(disposing);
    }
}
