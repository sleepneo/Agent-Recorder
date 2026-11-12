using System.Drawing;
using System.Text.Json;
using AgentRecorder.App;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.FutureWindowUiTestHost;

// Synthetic text and bitmaps only. No datastore, approval API, screen provider or backend.
internal static class StandingApprovalTestHost
{
    internal static StandingLeaseApprovalDetails Details => new("Test-only synthetic display",
        new(0, 0, 800, 600), new(10, 20, 64, 48), DateTimeOffset.UtcNow.AddMinutes(1),
        DateTimeOffset.UtcNow.AddMinutes(2), DateTimeOffset.UtcNow.AddMinutes(3), TimeSpan.FromSeconds(10),
        DateTimeOffset.UtcNow.AddMinutes(4), @"C:\TestOnly", "never-created.mp4");

    internal static int Run(string root, string mode)
    {
        Directory.CreateDirectory(root);
        var sync = new object();
        void Write(string stage, object data)
        {
            lock (sync) File.AppendAllText(Path.Combine(root, "audit.jsonl"),
                JsonSerializer.Serialize(new { stage, data }) + Environment.NewLine);
        }
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        System.Threading.Timer? timer = null;
        var preview = new Preview(mode == "standing-blocked-preview" ? release : null, mode == "standing-error-preview");
        var ui = new TrayStandingPlanSetupUi(() => new UiTextProvider(UiLanguage.EnUs), new()
        {
            PreviewProvider = preview, DisableApprovalForTests = true,
            DisplayBudget = TimeSpan.FromSeconds(2), PreviewBudget = TimeSpan.FromMilliseconds(100),
            BeforeShowForTests = mode == "standing-invisible" ? form =>
            {
                _ = form.Handle;
                release.Wait(TimeSpan.FromSeconds(12));
            } : null,
            AfterVisibleForTests = form =>
            {
                Write("fixture_readable", new { readable = form.ButtonsReadableForTests,
                    safe_enter = form.AcceptButtonIsRejectForTests, safe_escape = form.CancelButtonIsRejectForTests });
                timer = new(_ => cancel.Cancel(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
            },
        });
        var operation = ui.BeginApproval("standing-test-only", Details, Write, cancel.Token);
        try
        {
            var result = operation.Decision.WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult();
            Write("fixture_decision", new { outcome = result.Outcome.ToString(), result.Reason, retired = operation.Retired.IsCompleted });
            if (mode is "standing-invisible" or "standing-blocked-preview")
            {
                if (operation.Retired.IsCompleted) return 31;
                // Observe the still-owned native work before permitting actual exit.
                Thread.Sleep(150);
            }
            release.Set();
            operation.Retired.WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            Write("fixture_finished", new { preview_calls = preview.Calls });
            return mode == "standing-invisible"
                ? result is { Outcome: StandingLeaseApprovalOutcome.Unavailable, Reason: "lease_approval_display_timeout" } && preview.Calls == 0 ? 0 : 32
                : result.Outcome == StandingLeaseApprovalOutcome.Cancelled ? 0 : 33;
        }
        catch { return 34; }
        finally { release.Set(); cancel.Cancel(); timer?.Dispose(); }
    }

    internal sealed class Preview(ManualResetEventSlim? release = null, bool fail = false) : IScreenPreviewProvider
    {
        internal int Calls;
        public Bitmap Capture(ConfirmationCaptureBounds bounds, Size maxSize)
        {
            Interlocked.Increment(ref Calls);
            release?.Wait();
            if (fail) throw new InvalidOperationException("synthetic preview failure");
            return new Bitmap(32, 16);
        }
    }
}
