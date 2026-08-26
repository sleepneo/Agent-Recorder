#if DEBUG
using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.App;

/// <summary>
/// Debug-only real-desktop preview for the recording-status surfaces. It creates
/// only the indicator/stop forms and a small controller; no engine, API, capture,
/// encoder, audio helper, confirmation, or tray service is started.
/// </summary>
internal static class RecordingStatusStylePreviewHost
{
    private const string Argument = "--recording-status-style-preview";

    public static bool TryRun(string[] args)
    {
        if (!args.Any(arg => arg.Equals(Argument, StringComparison.OrdinalIgnoreCase)))
            return false;

        ApplicationConfiguration.Initialize();
        using var controller = new RecordingStatusPreviewController();
        controller.Show();
        Application.Run(controller);
        return true;
    }

    private sealed class RecordingStatusPreviewController : Form
    {
        private readonly AuditLogger _audit = new();
        private RecordingIndicatorManager? _manager;
        private bool _nested = true;
        private bool _finalizing;
        private bool _motion = true;
        private Button _nestedButton = null!;
        private Button _phaseButton = null!;
        private Button _motionButton = null!;
        private Label _stateSummary = null!;

        public RecordingStatusPreviewController()
        {
            Text = "Agent Recorder — Recording Status Style Preview";
            // Keep a normal top-level debug host so desktop automation can
            // reach the controller while the production overlay windows remain
            // tool windows and taskbar-free.
            FormBorderStyle = FormBorderStyle.FixedSingle;
            ControlBox = true;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            // Keep the controller within the bounded desktop-capture viewport
            // so every control remains reachable while the real overlay forms
            // are displayed separately on the same monitor.
            ClientSize = new Size(300, 205);

            var instructions = new Label
            {
                Dock = DockStyle.Top,
                Height = 58,
                Text = "Real production windows only\r\n" +
                       "IndicatorManager owns borders, REC labels, and stop capsules.\r\n" +
                       "Use controls to refresh the real windows; Esc exits.",
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(4),
                Font = new Font("Segoe UI", 8F)
            };
            Controls.Add(instructions);

            _stateSummary = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(24),
                AutoEllipsis = false
            };
            Controls.Add(_stateSummary);

            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 62,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(2)
            };
            for (int column = 0; column < buttons.ColumnCount; column++)
                buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / buttons.ColumnCount));
            for (int row = 0; row < buttons.RowCount; row++)
                buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / buttons.RowCount));
            _nestedButton = AddButton(buttons, "Nested", (_, _) => { _nested = true; RefreshPreview(); });
            buttons.SetCellPosition(_nestedButton, new TableLayoutPanelCellPosition(0, 0));
            var ordinaryButton = AddButton(buttons, "Ordinary", (_, _) => { _nested = false; RefreshPreview(); });
            buttons.SetCellPosition(ordinaryButton, new TableLayoutPanelCellPosition(1, 0));
            _phaseButton = AddButton(buttons, "Recording", (_, _) => { _finalizing = !_finalizing; RefreshPreview(); });
            buttons.SetCellPosition(_phaseButton, new TableLayoutPanelCellPosition(2, 0));
            _motionButton = AddButton(buttons, "Motion on", (_, _) => { _motion = !_motion; RefreshPreview(); });
            buttons.SetCellPosition(_motionButton, new TableLayoutPanelCellPosition(0, 1));
            var closeButton = AddButton(buttons, "Close", (_, _) => ClosePreview());
            buttons.SetCellPosition(closeButton, new TableLayoutPanelCellPosition(1, 1));
            Controls.Add(buttons);

            Shown += (_, _) =>
            {
                var screen = Screen.FromControl(this).WorkingArea;
                Location = new Point(screen.Left + 20, screen.Top + 20);
                RefreshPreview();
            };
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    ClosePreview();
                }
            };
            FormClosed += (_, _) =>
            {
                _manager?.CloseAll("debug_preview_closed");
                _manager = null;
            };
        }

        private Button AddButton(Control parent, string text, EventHandler handler)
        {
            var button = new Button
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = text,
                Padding = new Padding(4, 2, 4, 2),
                Margin = new Padding(2),
                Font = new Font("Segoe UI", 8F)
            };
            button.Click += handler;
            parent.Controls.Add(button);
            return button;
        }

        private void RefreshPreview()
        {
            // The old manager and all of its real tool windows are closed before
            // creating the next state, so refresh cannot accumulate 4+4 windows.
            _manager?.CloseAll("debug_preview_refresh");
            var motionPreference = new FixedRecordingMotionPreference(_motion);
            _manager = new RecordingIndicatorManager(
                _audit,
                _ => ClosePreview(),
                (id, presentation, started, duration, role, textFactory) =>
                    new RecordingIndicatorForm(
                        id,
                        presentation,
                        started,
                        duration,
                        role,
                        displayAffinity: null,
                        textProviderFactory: textFactory,
                        motionPreference: motionPreference),
                (id, bounds, size, dpi, mode, role) =>
                    new RecordingStopControlForm(
                        id,
                        bounds,
                        size,
                        dpi,
                        mode,
                        role,
                        new UiTextProvider(UiLanguageStore.LoadOrDefault())),
                new DisplayDpiResolver());

            // Keep the real production windows on the controller's monitor so
            // a desktop reviewer can see the actual overlays and this state panel
            // in one physical preview session, even with multiple monitors.
            var previewScreen = Screen.FromControl(this).Bounds;
            int outerWidth = Math.Min(900, Math.Max(640, previewScreen.Width / 2));
            int outerHeight = Math.Min(620, Math.Max(420, previewScreen.Height - 220));
            outerWidth = Math.Min(outerWidth, previewScreen.Width);
            outerHeight = Math.Min(outerHeight, previewScreen.Height);
            int outerX = previewScreen.Right - outerWidth - 60;
            int outerY = previewScreen.Y + 180;

            var outer = new RecordingUiPresentation
            {
                RecordingId = "preview_outer",
                State = RecordingUiState.Recording,
                SourceType = "debug_preview",
                CaptureBounds = new RecordingUiBounds(outerX, outerY, outerWidth, outerHeight),
                StartedAtUtc = DateTime.UtcNow,
                NestedRole = "outer",
                NestedSessionId = "preview_session"
            };

            if (_nested)
            {
                _manager.ShowFor(outer);
                var inner = new RecordingUiPresentation
                {
                    RecordingId = "preview_inner",
                    State = RecordingUiState.Recording,
                    SourceType = "debug_preview",
                    CaptureBounds = new RecordingUiBounds(
                        outerX + Math.Min(240, Math.Max(80, outerWidth / 5)),
                        outerY + Math.Min(190, Math.Max(70, outerHeight / 5)),
                        Math.Max(260, outerWidth - Math.Min(420, Math.Max(160, outerWidth / 3))),
                        Math.Max(180, outerHeight - Math.Min(330, Math.Max(140, outerHeight / 3)))),
                    StartedAtUtc = DateTime.UtcNow,
                    NestedRole = "inner",
                    ParentRecordingId = outer.RecordingId,
                    NestedSessionId = outer.NestedSessionId
                };
                _manager.ShowFor(inner, outer);
            }
            else
            {
                _manager.ShowFor(outer with { RecordingId = "preview_recording", NestedRole = null, NestedSessionId = null });
            }

            foreach (var indicator in _manager.IndicatorsForTests.Values)
            {
                if (_finalizing)
                    indicator.SetPhase(RecordingIndicatorPhase.Finalizing);
                else
                    indicator.SetPhase(RecordingIndicatorPhase.Recording);
            }

            _nestedButton.Text = _nested ? "Nested ✓" : "Nested";
            _phaseButton.Text = _finalizing ? "Finalizing" : "Recording";
            _motionButton.Text = _motion ? "Motion on" : "Motion off";
            var counts = RecordingStatusPreviewState.Capture(_manager);
            if (!counts.Matches(_nested))
                throw new InvalidOperationException("Debug preview produced an unexpected real window count.");
            _stateSummary.Text = RecordingStatusPreviewState.Describe(_nested, _finalizing, _motion, counts);
        }

        private void ClosePreview()
        {
            _manager?.CloseAll("debug_preview_stop_clicked");
            Close();
        }
    }
}
#endif
