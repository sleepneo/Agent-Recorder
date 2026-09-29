using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using Xunit;

namespace AgentRecorder.Tests;

public class RequiredOnceExecutionApprovalFormTests
{
    private static readonly Rectangle Primary = new(0, 0, 1600, 900);
    private static readonly Rectangle Secondary = new(-1280, 0, 1280, 800);

    [Fact]
    public void Show_AttemptsForegroundAndKeepsFullConfirmationDetailsOutOfAudit()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAudit();
            var activator = new FakeWindowActivator();
            using var form = CreateForm(audit, activator);

            form.Show();
            Application.DoEvents();

            Assert.True(form.Visible);
            Assert.True(form.ShowInTaskbar);
            Assert.True(activator.TopMostCalls > 0);
            Assert.True(activator.ForegroundCalls > 0);
            Assert.Contains(audit.Events, item => item.Event.EndsWith("handle_created", StringComparison.Ordinal));
            Assert.Contains(audit.Events, item => item.Event.EndsWith("shown", StringComparison.Ordinal));
            Assert.Contains(audit.Events, item => item.Event.EndsWith("foreground_attempt", StringComparison.Ordinal));
            Assert.Contains(audit.Events, item => item.Event.EndsWith("foreground_result", StringComparison.Ordinal));
            Assert.DoesNotContain(audit.Events, item => item.Event.StartsWith(
                "required_once_execution_dialog.required_once_execution_dialog.", StringComparison.Ordinal));
            Assert.Equal("required_once_execution_dialog.shown",
                TrayRequiredOncePlanExecutionUi.NormalizeAuditEventName("required_once_execution_dialog.shown"));
            Assert.Equal("required_once_execution_dialog.shown",
                TrayRequiredOncePlanExecutionUi.NormalizeAuditEventName("shown"));
            Assert.True(Secondary.Contains(form.Bounds));

            var renderedDetails = FindText(form);
            Assert.Contains("120 seconds", renderedDetails, StringComparison.Ordinal);
            Assert.Contains("Not captured", renderedDetails, StringComparison.Ordinal);
            Assert.Contains("X -1170, Y 220", renderedDetails, StringComparison.Ordinal);
            Assert.Contains("D:\\private\\secret\\capture.mp4", renderedDetails, StringComparison.Ordinal);
            Assert.DoesNotContain("D:\\private\\secret\\capture.mp4", audit.SerializedEvents, StringComparison.Ordinal);

            form.SimulateApproveMouseClickForTest();
            Application.DoEvents();
            Assert.Equal(RequiredOnceExecutionApprovalResult.Approved, form.Result);
            Assert.Contains(audit.Events, item => item.Event.EndsWith("user_approved", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(Keys.Enter)]
    [InlineData(Keys.Escape)]
    public void EnterAndEscapeNeverApproveEvenWithApproveButtonFocused(Keys key)
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAudit();
            using var form = CreateForm(audit, new FakeWindowActivator { ForegroundAllowed = false });
            form.Show();
            Application.DoEvents();
            form.ActiveControl = form.ApproveButtonForTests;
            form.ApproveButtonForTests.PerformClick();
            Application.DoEvents();
            Assert.True(form.Visible);
            Assert.NotEqual(RequiredOnceExecutionApprovalResult.Approved, form.Result);

            form.ProcessKeyForTest(key);
            Application.DoEvents();

            Assert.Equal(RequiredOnceExecutionApprovalResult.Rejected, form.Result);
            Assert.DoesNotContain(audit.Events, item => item.Event.EndsWith("user_approved", StringComparison.Ordinal));
            Assert.Contains(audit.Events, item => item.Event.EndsWith("user_rejected", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ClosingWindowIsRejectAndNeverApprove()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAudit();
            var form = CreateForm(audit, new FakeWindowActivator { ForegroundAllowed = false });
            form.Show();
            Application.DoEvents();
            form.Close();
            Application.DoEvents();

            Assert.Equal(RequiredOnceExecutionApprovalResult.Rejected, form.Result);
            Assert.DoesNotContain(audit.Events, item => item.Event.EndsWith("user_approved", StringComparison.Ordinal));
            Assert.Contains(audit.Events, item => item.Event.EndsWith("user_rejected", StringComparison.Ordinal));
            form.Dispose();
        });
    }

    [Fact]
    public void CancellationBeforeHandleCreationDoesNotShowWindow()
    {
        RunOnSta(() =>
        {
            using var cancellation = new CancellationTokenSource();
            var audit = new CaptureAudit();
            var result = RequiredOnceExecutionApprovalForm.ShowModal(
                Details(), cancellation.Token, "cancel-before", new FakeWindowActivator(), audit.Log,
                () => Details().ScheduledStartUtc, () => new[] { Primary },
                beforeHandleForTest: cancellation.Cancel);

            Assert.Equal(RequiredOnceExecutionApprovalResult.HostShutdown, result);
            Assert.DoesNotContain(audit.Events, item => item.Event.EndsWith("handle_created", StringComparison.Ordinal));
            Assert.DoesNotContain(audit.Events, item => item.Event.EndsWith("shown", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void CancellationAfterHandleCreationClosesWithoutShowingOrLeavingGhostWindow()
    {
        RunOnSta(() =>
        {
            using var cancellation = new CancellationTokenSource();
            var audit = new CaptureAudit();
            var result = RequiredOnceExecutionApprovalForm.ShowModal(
                Details(), cancellation.Token, "cancel-after", new FakeWindowActivator(), audit.Log,
                () => Details().ScheduledStartUtc, () => new[] { Primary },
                afterHandleForTest: cancellation.Cancel);

            Assert.Equal(RequiredOnceExecutionApprovalResult.HostShutdown, result);
            Assert.Contains(audit.Events, item => item.Event.EndsWith("handle_created", StringComparison.Ordinal));
            Assert.DoesNotContain(audit.Events, item => item.Event.EndsWith("shown", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void LatestStartTimeoutClosesDialogAndLogsBoundedTimeout()
    {
        RunOnSta(() =>
        {
            var now = Details().ScheduledStartUtc;
            var audit = new CaptureAudit();
            using var form = CreateForm(audit, new FakeWindowActivator { ForegroundAllowed = false }, () => now);
            form.Show();
            Application.DoEvents();
            form.RetryForegroundForTest();
            Assert.True(form.Visible);
            Assert.False(form.IsCompletedForTests);
            now = Details().LatestStartUtc;

            form.CheckDeadlineForTest();
            Application.DoEvents();

            Assert.Equal(RequiredOnceExecutionApprovalResult.TimedOut, form.Result);
            Assert.Contains(audit.Events, item => item.Event.EndsWith("timeout", StringComparison.Ordinal));
            Assert.DoesNotContain(audit.Events, item => item.Event.EndsWith("user_approved", StringComparison.Ordinal));
            Assert.False(form.DeadlineTimerEnabledForTests);
        });
    }

    [Fact]
    public void PromptTimeoutExpiresBeforeCoordinatorFallback()
    {
        RunOnSta(() =>
        {
            var details = Details() with { LatestStartUtc = Details().ScheduledStartUtc.AddMinutes(2) };
            var now = details.ScheduledStartUtc;
            var audit = new CaptureAudit();
            using var form = new RequiredOnceExecutionApprovalForm(
                details, CancellationToken.None, "bounded-prompt", new FakeWindowActivator(), audit.Log,
                () => now, () => new[] { Primary, Secondary });
            form.Show();
            Application.DoEvents();
            now = details.ScheduledStartUtc.AddSeconds(55);

            form.CheckDeadlineForTest();
            Application.DoEvents();

            Assert.Equal(RequiredOnceExecutionApprovalResult.TimedOut, form.Result);
            Assert.Contains("execution_confirmation_timed_out", audit.SerializedEvents, StringComparison.Ordinal);
            Assert.DoesNotContain(audit.Events, item => item.Event.EndsWith("user_approved", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void ForegroundDenialKeepsVisibleDialogAvailableForExplicitClick()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAudit();
            var activator = new FakeWindowActivator { ForegroundAllowed = false };
            var form = CreateForm(audit, activator);
            form.Show();
            Application.DoEvents();
            Assert.True(form.Visible);

            form.RetryForegroundForTest();
            Application.DoEvents();

            Assert.True(form.Visible);
            Assert.True(form.TopMost);
            Assert.True(form.ShowInTaskbar);
            Assert.True(Secondary.Contains(form.Bounds));
            Assert.False(form.IsCompletedForTests);
            Assert.Equal(2, form.ForegroundAttemptsForTests);
            Assert.Contains(audit.Events, item => item.Event.EndsWith("foreground_unconfirmed_but_visible", StringComparison.Ordinal));
            Assert.DoesNotContain(audit.Events, item => item.Event.EndsWith("unavailable", StringComparison.Ordinal));

            form.SimulateApproveMouseClickForTest();
            Application.DoEvents();
            Assert.Equal(RequiredOnceExecutionApprovalResult.Approved, form.Result);
            Assert.Contains(audit.Events, item => item.Event.EndsWith("user_approved", StringComparison.Ordinal));
            form.Dispose();
        });
    }

    [Fact]
    public void PlacementClampsDpiScaledWindowAndUsesApprovedNegativeCoordinateDisplay()
    {
        var areas = new[] { Primary, Secondary };
        var scaled = RequiredOnceExecutionApprovalForm.ComputeVisibleBounds(
            new Rectangle(-1100, 100, 1000, 600), new Size(1800, 1200), areas, Primary);
        Assert.True(Secondary.Contains(scaled));
        Assert.True(scaled.Width <= Secondary.Width - 24);
        Assert.True(scaled.Height <= Secondary.Height - 24);

        var offscreen = RequiredOnceExecutionApprovalForm.ComputeVisibleBounds(
            new Rectangle(9000, -4000, 800, 600), new Size(900, 700), areas, Primary);
        Assert.True(Primary.Contains(offscreen));
    }

    private static RequiredOnceExecutionApprovalForm CreateForm(
        CaptureAudit audit,
        FakeWindowActivator activator,
        Func<DateTimeOffset>? clock = null) => new(
            Details(), CancellationToken.None, "test-request", activator, audit.Log,
            clock ?? (() => Details().ScheduledStartUtc), () => new[] { Primary, Secondary });

    private static RequiredOnceExecutionApprovalDetails Details() => new(
        "Test Display", "test-fingerprint", new AuthorizedPhysicalRectangle(-1280, 0, 1280, 800),
        new AuthorizedPhysicalRectangle(110, 220, 800, 450),
        new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 27, 10, 1, 0, TimeSpan.Zero),
        TimeSpan.FromSeconds(120), "D:\\private\\secret\\capture.mp4");

    private static string FindText(Control root) => string.Join("\n", new[] { root.Text }
        .Concat(root.Controls.Cast<Control>().Select(FindText)));

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { exception = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA UI test did not finish.");
        if (exception is not null) throw new TargetInvocationException(exception);
    }

    private sealed class CaptureAudit
    {
        private readonly ConcurrentQueue<(string Event, object Payload)> _events = new();
        internal IReadOnlyCollection<(string Event, object Payload)> Events => _events.ToArray();
        internal string SerializedEvents => string.Join("\n", _events.Select(item => JsonSerializer.Serialize(item.Payload)));
        internal void Log(string evt, object payload) => _events.Enqueue((evt, payload));
    }

    private sealed class FakeWindowActivator : IWindowActivator
    {
        private IntPtr _foreground;
        internal bool ForegroundAllowed { get; init; } = true;
        internal int TopMostCalls { get; private set; }
        internal int ForegroundCalls { get; private set; }
        public bool SetTopMost(IntPtr hWnd) { TopMostCalls++; return true; }
        public bool SetForeground(IntPtr hWnd)
        {
            ForegroundCalls++;
            if (ForegroundAllowed) _foreground = hWnd;
            return ForegroundAllowed;
        }
        public bool BringToTop(IntPtr hWnd)
        {
            if (ForegroundAllowed) _foreground = hWnd;
            return ForegroundAllowed;
        }
        public IntPtr GetForegroundWindow() => _foreground;
    }
}
