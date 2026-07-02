using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

/// <summary>
/// Full-screen region selection form with drag-to-create, move, and resize handles.
/// Uses Windows virtual screen coordinates (SystemInformation.VirtualScreen) to support
/// multi-monitor setups including negative coordinate displays.
/// Returns DialogResult.OK with virtual screen Bounds set, or DialogResult.Cancel.
/// </summary>
public sealed class RegionSelectionForm : Form
{
    private enum DragMode
    {
        None,
        Create,
        Move,
        ResizeNW, ResizeN, ResizeNE,
        ResizeE, ResizeW,
        ResizeSW, ResizeS, ResizeSE
    }

    private Rectangle _selection = Rectangle.Empty;
    private DragMode _dragMode = DragMode.None;
    private Point _dragStart;
    private Rectangle _dragOrig;
    private readonly Label _infoLabel;
    private readonly Label _coordsLabel;
    private readonly Button _confirmButton;
    private readonly Button _cancelButton;
    private const int HandleSize = 10;
    private const int MinSize = 32;

    /// <summary>
    /// Selected bounds in virtual screen coordinates.
    /// </summary>
    public Rectangle SelectedBounds { get; private set; }

    /// <summary>
    /// Display ID where the selection is located.
    /// </summary>
    public string DisplayId { get; private set; } = "";

    /// <summary>
    /// Coordinate space of the returned bounds.
    /// </summary>
    public string CoordinateSpace { get; private set; } = "virtual_screen";

    public RegionSelectionForm()
    {
        // Initialize button fields FIRST (null guards) before any property that could trigger OnResize
        _confirmButton = new Button
        {
            Text = "Confirm (Enter)",
            Size = new Size(140, 40),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(0, 150, 0),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Enabled = false,
            Cursor = Cursors.Hand,
            Visible = true
        };
        _confirmButton.FlatAppearance.BorderSize = 1;
        _confirmButton.FlatAppearance.BorderColor = Color.White;
        _confirmButton.Click += (_, _) => ConfirmSelection();

        _cancelButton = new Button
        {
            Text = "Cancel (Esc)",
            Size = new Size(140, 40),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(180, 0, 0),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Visible = true
        };
        _cancelButton.FlatAppearance.BorderSize = 1;
        _cancelButton.FlatAppearance.BorderColor = Color.White;
        _cancelButton.Click += (_, _) => CancelSelection();

        // Info label
        _infoLabel = new Label
        {
            Text = "Click and drag to select a region. Press Enter to confirm, Esc to cancel.",
            ForeColor = Color.White,
            BackColor = Color.FromArgb(150, 0, 0, 0),
            Padding = new Padding(12),
            AutoSize = true,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Location = new Point(20, 20)
        };

        // Coordinates label (virtual screen coordinates)
        _coordsLabel = new Label
        {
            Text = "Virtual: X=0, Y=0, W=0, H=0",
            ForeColor = Color.Cyan,
            BackColor = Color.FromArgb(150, 0, 0, 0),
            Padding = new Padding(12),
            AutoSize = true,
            Font = new Font("Consolas", 11),
            Location = new Point(20, 65)
        };

        // Add controls BEFORE setting bounds to avoid early OnResize calls
        Controls.Add(_infoLabel);
        Controls.Add(_coordsLabel);
        Controls.Add(_confirmButton);
        Controls.Add(_cancelButton);

        // Now set form properties
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.3;
        DoubleBuffered = true;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true; // Ensure key events are captured even when child controls have focus

        // Use virtual screen bounds to cover all monitors including negative coordinates
        var virtualScreen = SystemInformation.VirtualScreen;
        Bounds = virtualScreen;
    }

    private void UpdateButtonPositions()
    {
        // Place buttons at the TOP center of the form so they are always visible
        // regardless of screen size or DPI scaling
        int topY = 110;
        int centerX = Width / 2;
        _confirmButton.Location = new Point(centerX - 150, topY);
        _cancelButton.Location = new Point(centerX + 10, topY);
        _confirmButton.BringToFront();
        _cancelButton.BringToFront();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateButtonPositions();
        Invalidate();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Ensure window is foreground and has focus
        BringToFront();
        Activate();
        Focus();
        UpdateButtonPositions();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && _confirmButton.Enabled)
        {
            ConfirmSelection();
        }
        else if (e.KeyCode == Keys.Escape)
        {
            CancelSelection();
        }
    }

    private void ConfirmSelection()
    {
        if (_selection.Width < MinSize || _selection.Height < MinSize)
        {
            _infoLabel.Text = $"Selection too small. Minimum size is {MinSize}x{MinSize} pixels.";
            return;
        }

        // Normalize to even dimensions (x264/yuv420p requirement)
        int normalizedW = _selection.Width;
        int normalizedH = _selection.Height;
        if (normalizedW % 2 != 0) normalizedW--;
        if (normalizedH % 2 != 0) normalizedH--;

        // Ensure still at least MinSize after normalization
        if (normalizedW < MinSize) normalizedW = MinSize;
        if (normalizedH < MinSize) normalizedH = MinSize;

        // Convert from client area coordinates to virtual screen coordinates.
        // _selection is relative to form's client area (0,0 at form's top-left),
        // but we need absolute virtual screen coordinates where primary display
        // may start at (0,0) but secondary displays can have negative X.
        int virtualX = Bounds.X + _selection.X;
        int virtualY = Bounds.Y + _selection.Y;
        SelectedBounds = new Rectangle(virtualX, virtualY, normalizedW, normalizedH);

        // Find which display this selection is on
        DisplayId = FindDisplayForBounds(SelectedBounds);

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Finds the display that contains the center of the given bounds.
    /// Returns the display_id from SystemQuery.EnumDisplays().
    /// </summary>
    private static string FindDisplayForBounds(Rectangle bounds)
    {
        // Calculate center of bounds
        int centerX = bounds.X + bounds.Width / 2;
        int centerY = bounds.Y + bounds.Height / 2;

        try
        {
            var displays = SystemQuery.EnumDisplays();
            foreach (var display in displays)
            {
                var db = display.bounds;
                // Check if center is within this display (using virtual screen coordinates)
                if (centerX >= db.x && centerX < db.x + db.width &&
                    centerY >= db.y && centerY < db.y + db.height)
                {
                    return display.id;
                }
            }
        }
        catch
        {
            // Ignore enumeration errors
        }

        // Default to first display if no match
        return "display_1";
    }

    private void CancelSelection()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left) return;

        var mode = GetHitTest(e.Location);
        if (mode == DragMode.None)
        {
            // Start creating a new selection
            _dragMode = DragMode.Create;
            _dragStart = e.Location;
            _selection = new Rectangle(e.X, e.Y, 0, 0);
            _confirmButton.Enabled = false;
        }
        else
        {
            _dragMode = mode;
            _dragStart = e.Location;
            _dragOrig = _selection;
        }

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragMode == DragMode.None)
        {
            // Update cursor based on hover
            var mode = GetHitTest(e.Location);
            Cursor = mode switch
            {
                DragMode.Move => Cursors.SizeAll,
                DragMode.ResizeNW or DragMode.ResizeSE => Cursors.SizeNWSE,
                DragMode.ResizeNE or DragMode.ResizeSW => Cursors.SizeNESW,
                DragMode.ResizeN or DragMode.ResizeS => Cursors.SizeNS,
                DragMode.ResizeE or DragMode.ResizeW => Cursors.SizeWE,
                _ => Cursors.Cross
            };
            return;
        }

        if (_dragMode == DragMode.Create)
        {
            int x = Math.Min(_dragStart.X, e.X);
            int y = Math.Min(_dragStart.Y, e.Y);
            int w = Math.Abs(e.X - _dragStart.X);
            int h = Math.Abs(e.Y - _dragStart.Y);
            _selection = new Rectangle(x, y, w, h);
        }
        else if (_dragMode == DragMode.Move)
        {
            int dx = e.X - _dragStart.X;
            int dy = e.Y - _dragStart.Y;
            int newX = _dragOrig.X + dx;
            int newY = _dragOrig.Y + dy;

            // Clamp to virtual screen bounds
            newX = Math.Max(0, Math.Min(Width - _dragOrig.Width, newX));
            newY = Math.Max(0, Math.Min(Height - _dragOrig.Height, newY));

            _selection = new Rectangle(newX, newY, _dragOrig.Width, _dragOrig.Height);
        }
        else
        {
            ResizeSelection(e.Location);
        }

        UpdateInfoLabel();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Left && _dragMode != DragMode.None)
        {
            if (_selection.Width > 0 && _selection.Height > 0)
            {
                _confirmButton.Enabled = true;
            }
            _dragMode = DragMode.None;
            Invalidate();
        }
    }

    private DragMode GetHitTest(Point p)
    {
        if (_selection.IsEmpty || _selection.Width < MinSize || _selection.Height < MinSize)
            return DragMode.None;

        int hs = HandleSize;

        // Corner handles
        if (IsWithin(p, _selection.Left - hs, _selection.Top - hs, hs * 2, hs * 2))
            return DragMode.ResizeNW;
        if (IsWithin(p, _selection.Right - hs, _selection.Top - hs, hs * 2, hs * 2))
            return DragMode.ResizeNE;
        if (IsWithin(p, _selection.Left - hs, _selection.Bottom - hs, hs * 2, hs * 2))
            return DragMode.ResizeSW;
        if (IsWithin(p, _selection.Right - hs, _selection.Bottom - hs, hs * 2, hs * 2))
            return DragMode.ResizeSE;

        // Edge handles
        if (IsWithin(p, _selection.Left + hs, _selection.Top - hs, _selection.Width - hs * 2, hs * 2))
            return DragMode.ResizeN;
        if (IsWithin(p, _selection.Left + hs, _selection.Bottom - hs, _selection.Width - hs * 2, hs * 2))
            return DragMode.ResizeS;
        if (IsWithin(p, _selection.Left - hs, _selection.Top + hs, hs * 2, _selection.Height - hs * 2))
            return DragMode.ResizeW;
        if (IsWithin(p, _selection.Right - hs, _selection.Top + hs, hs * 2, _selection.Height - hs * 2))
            return DragMode.ResizeE;

        // Interior (move)
        if (p.X >= _selection.Left + hs && p.X <= _selection.Right - hs &&
            p.Y >= _selection.Top + hs && p.Y <= _selection.Bottom - hs)
            return DragMode.Move;

        return DragMode.None;
    }

    private static bool IsWithin(Point p, int x, int y, int w, int h)
    {
        return p.X >= x && p.X <= x + w && p.Y >= y && p.Y <= y + h;
    }

    private void ResizeSelection(Point p)
    {
        int left = _dragOrig.Left;
        int right = _dragOrig.Right;
        int top = _dragOrig.Top;
        int bottom = _dragOrig.Bottom;

        switch (_dragMode)
        {
            case DragMode.ResizeNW: left = p.X; top = p.Y; break;
            case DragMode.ResizeN: top = p.Y; break;
            case DragMode.ResizeNE: right = p.X; top = p.Y; break;
            case DragMode.ResizeW: left = p.X; break;
            case DragMode.ResizeE: right = p.X; break;
            case DragMode.ResizeSW: left = p.X; bottom = p.Y; break;
            case DragMode.ResizeS: bottom = p.Y; break;
            case DragMode.ResizeSE: right = p.X; bottom = p.Y; break;
        }

        int newW = right - left;
        int newH = bottom - top;

        if (newW < MinSize)
        {
            if (_dragMode is DragMode.ResizeNW or DragMode.ResizeW or DragMode.ResizeSW)
                left = right - MinSize;
            else
                right = left + MinSize;
        }
        if (newH < MinSize)
        {
            if (_dragMode is DragMode.ResizeNW or DragMode.ResizeN or DragMode.ResizeNE)
                top = bottom - MinSize;
            else
                bottom = top + MinSize;
        }

        // Clamp to virtual screen bounds
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(Width, right);
        bottom = Math.Min(Height, bottom);

        _selection = new Rectangle(left, top, right - left, bottom - top);
    }

    private void UpdateInfoLabel()
    {
        if (_selection.Width > 0 && _selection.Height > 0)
        {
            // Convert client area coordinates to virtual screen coordinates for display
            int virtualX = Bounds.X + _selection.X;
            int virtualY = Bounds.Y + _selection.Y;
            _infoLabel.Text = $"Virtual Screen: X={virtualX}, Y={virtualY}, W={_selection.Width}, H={_selection.Height}  |  Enter to confirm, Esc to cancel";
            _coordsLabel.Text = $"Form Bounds: ({Bounds.X}, {Bounds.Y}) -> ({Bounds.Right}, {Bounds.Bottom})";
        }
        else
        {
            _infoLabel.Text = "Click and drag to select a region. Press Enter to confirm, Esc to cancel.";
            _coordsLabel.Text = $"Virtual Screen Bounds: ({Bounds.X}, {Bounds.Y}, {Bounds.Width}x{Bounds.Height})";
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;

        if (_selection.Width > 0 && _selection.Height > 0)
        {
            // Draw dark overlay outside selection
            using var region = new Region(ClientRectangle);
            region.Exclude(_selection);
            using var brush = new SolidBrush(Color.FromArgb(100, 0, 0, 0));
            g.FillRegion(brush, region);

            // Draw selection border
            using var pen = new Pen(Color.Yellow, 2);
            g.DrawRectangle(pen, _selection);

            // Draw resize handles
            using var handleBrush = new SolidBrush(Color.Yellow);
            int hs = HandleSize;
            g.FillRectangle(handleBrush, _selection.Left - hs / 2, _selection.Top - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Right - hs / 2, _selection.Top - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Left - hs / 2, _selection.Bottom - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Right - hs / 2, _selection.Bottom - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Left + _selection.Width / 2 - hs / 2, _selection.Top - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Left + _selection.Width / 2 - hs / 2, _selection.Bottom - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Left - hs / 2, _selection.Top + _selection.Height / 2 - hs / 2, hs, hs);
            g.FillRectangle(handleBrush, _selection.Right - hs / 2, _selection.Top + _selection.Height / 2 - hs / 2, hs, hs);
        }
    }

    /// <summary>
    /// Show region selection dialog on a new STA thread.
    /// Returns bounds in virtual screen coordinates.
    /// </summary>
    public static (DialogResult Result, Rectangle Bounds, string DisplayId) ShowSelection()
    {
        Rectangle bounds = Rectangle.Empty;
        string displayId = "";
        DialogResult result = DialogResult.Cancel;

        var thread = new Thread(() =>
        {
            using var form = new RegionSelectionForm();
            result = form.ShowDialog();
            bounds = form.SelectedBounds;
            displayId = form.DisplayId;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return (result, bounds, displayId);
    }
}
