#if DEBUG
using System.Drawing;
using System.Windows.Forms;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

/// <summary>
/// Debug-only deterministic visual entry point. It intentionally bypasses the
/// tray, API and recording engine; preview windows can only reject.
/// </summary>
internal static class ConfirmationThemePreviewHost
{
    private const string ArgumentPrefix = "--confirmation-theme-preview";
    private const string CountdownArgumentPrefix = "--confirmation-countdown-preview";

    public static bool TryRun(string[] args)
    {
        string? value = args.FirstOrDefault(arg =>
            arg.StartsWith(ArgumentPrefix, StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith(CountdownArgumentPrefix, StringComparison.OrdinalIgnoreCase));
        if (value == null)
            return false;

        bool countdownPreview = value.StartsWith(CountdownArgumentPrefix, StringComparison.OrdinalIgnoreCase);
        var kind = ParseKind(value);
        int timeoutSeconds = countdownPreview ? 12 : 300;
        ApplicationConfiguration.Initialize();

        var provider = new FixedConfirmationThemeProvider(kind);
        var now = DateTime.UtcNow;
        var presentation = new RecordingConfirmationPresentation
        {
            Summary = new RecordingRequestSummary
            {
                Mode = "video",
                Source = "display",
                Audio = "No audio",
                AudioSourceKind = "none",
                Duration = "Manual stop",
                CountdownSeconds = 0,
                Output = "C:\\AgentRecorder\\preview\\confirmation-preview.mp4"
            },
            RecordingId = "theme-preview-recording",
            ConfirmationId = "theme-preview-confirmation",
            TimeoutSeconds = timeoutSeconds,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(timeoutSeconds),
            SourceType = "display",
            SourceTitle = "Confirmation theme preview",
            SourceApplication = "Agent Recorder",
            CoordinateSpace = "virtual_screen",
            CaptureSemantics = "window_surface",
            PreviewSemantics = "DWM window preview",
            PlannedBackend = "preview-only",
            TargetDisplayId = "preview-display",
            OutputKind = "mp4_file"
        };

        ConfirmationDecision? decision = null;
        var item = new PendingConfirmationItem(
            presentation,
            result =>
            {
                decision = result;
                Application.ExitThread();
            });

        using var form = new ConfirmationForm(
            item,
            queuePosition: 1,
            totalCount: 1,
            onResult: null,
            themeProvider: provider,
            previewOnly: true);
        form.Text = countdownPreview
            ? $"Agent Recorder — Confirmation Countdown Preview ({kind})"
            : $"Agent Recorder — Confirmation Preview ({kind})";
        Application.Run(form);

        // Closing by the host is still fail-closed. The form's FormClosing
        // handler has already converted it to a rejection before this point.
        _ = decision;
        return true;
    }

    private static ConfirmationThemeKind ParseKind(string argument)
    {
        int separator = argument.IndexOf('=');
        if (separator < 0)
            return ConfirmationThemeKind.Light;

        var value = argument[(separator + 1)..].Trim();
        return value.Equals("dark", StringComparison.OrdinalIgnoreCase)
            ? ConfirmationThemeKind.Dark
            : value.Equals("high-contrast", StringComparison.OrdinalIgnoreCase) ||
              value.Equals("highcontrast", StringComparison.OrdinalIgnoreCase)
                ? ConfirmationThemeKind.HighContrast
                : ConfirmationThemeKind.Light;
    }

    private sealed class FixedConfirmationThemeProvider : IConfirmationThemeProvider
    {
        private readonly ConfirmationThemeSnapshot _snapshot;

        public event EventHandler? ThemeChanged
        {
            add { }
            remove { }
        }

        public FixedConfirmationThemeProvider(ConfirmationThemeKind kind)
        {
            _snapshot = new ConfirmationThemeSnapshot(kind, ConfirmationThemePalette.For(kind));
        }

        public ConfirmationThemeSnapshot Resolve() => _snapshot;

        public void Dispose() { }
    }
}
#endif
