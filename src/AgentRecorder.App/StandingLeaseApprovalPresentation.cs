using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal enum StandingLeaseApprovalOutcome { Approved, Rejected, Cancelled, TimedOut, Unavailable }
internal sealed record StandingLeaseApprovalResult(StandingLeaseApprovalOutcome Outcome, string Reason);
internal sealed record StandingLeaseApprovalOperation(Task<StandingLeaseApprovalResult> Decision, Task Retired);

// Internal fixtures only; neither HTTP nor environment variables configure these seams.
internal sealed class StandingLeaseApprovalOptions
{
    internal IScreenPreviewProvider? PreviewProvider { get; init; }
    internal IFutureWindowApprovalWindowPlatform? WindowPlatform { get; init; }
    internal Action? BeforeConstructForTests { get; init; }
    internal Action<StandingLeaseApprovalForm>? BeforeShowForTests { get; init; }
    internal Action<StandingLeaseApprovalForm>? AfterVisibleForTests { get; init; }
    internal TimeSpan DisplayBudget { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan ApprovalBudget { get; init; } = TimeSpan.FromMinutes(5);
    internal TimeSpan PreviewBudget { get; init; } = TimeSpan.FromSeconds(1);
    internal bool DisableApprovalForTests { get; init; }
    internal Rectangle? WorkingAreaForTests { get; init; }
}

internal sealed class StandingLeaseApprovalAudit(string setupId, Action<string, object> write)
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    internal void Log(string stage, string? reason = null, nint handle = default,
        FutureWindowApprovalNativeState? state = null)
    {
        try
        {
            write("standing_setup.lease_approval." + stage, new
            {
                setup_id = setupId, phase = stage, process_id = Environment.ProcessId,
                thread_id = GetCurrentThreadId(), desktop = DesktopName(), hwnd = handle.ToInt64(),
                elapsed_ms = _elapsed.ElapsedMilliseconds, reason_code = reason,
                native_visible = state?.IsVisible, minimized = state?.IsMinimized,
                within_working_area = state?.IsWithinWorkingArea,
                bounds = state is null ? null : new { x = state.Bounds.X, y = state.Bounds.Y, width = state.Bounds.Width, height = state.Bounds.Height },
            });
        }
        catch { /* Diagnostics never decide or extend authorization. */ }
    }

    private static string DesktopName()
    {
        var name = new StringBuilder(128);
        return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _)
            ? name.ToString() : "unavailable";
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
}

// The worker owns any bitmap until a UI-thread transfer. Abandoning is not retirement.
internal sealed class StandingLeasePreviewOwner
{
    private readonly object _gate = new();
    private readonly Thread _worker;
    private bool _started, _abandoned, _completed;
    private Bitmap? _bitmap;
    internal StandingLeasePreviewOwner(Func<Bitmap?> capture, StandingLeaseApprovalAudit audit, CancellationToken cancellation)
    {
        _worker = new Thread(() =>
        {
            lock (_gate) { if (_abandoned || cancellation.IsCancellationRequested) { _completed = true; return; } }
            audit.Log("preview_begin");
            Bitmap? result = null;
            try { result = capture(); }
            catch { audit.Log("preview_failed", "lease_preview_unavailable"); }
            lock (_gate)
            {
                if (_abandoned) result?.Dispose();
                else _bitmap = result;
                _completed = true;
            }
            audit.Log("preview_returning", result is null ? "lease_preview_unavailable" : null);
        }) { IsBackground = true, Name = "StandingLeasePreview" };
        try { _worker.Start(); _started = true; }
        catch { _completed = true; audit.Log("preview_failed", "lease_preview_worker_unavailable"); }
    }
    internal bool TryTake(out Bitmap? bitmap)
    {
        lock (_gate)
        {
            bitmap = null;
            if (!_completed || _abandoned) return false;
            bitmap = _bitmap; _bitmap = null; _abandoned = true;
            return true;
        }
    }
    internal void Abandon()
    {
        lock (_gate) { _abandoned = true; _bitmap?.Dispose(); _bitmap = null; }
    }
    internal void Join() { if (_started) _worker.Join(); Abandon(); }
}

internal static class StandingLeaseApprovalPresentation
{
    internal static StandingLeaseApprovalOperation Start(string setupId, StandingLeaseApprovalDetails details,
        IUiTextProvider text, Action<string, object> audit, CancellationToken cancellationToken,
        StandingLeaseApprovalOptions? options = null)
    {
        options ??= new();
        var trace = new StandingLeaseApprovalAudit(setupId, audit);
        var decision = new TaskCompletionSource<StandingLeaseApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new CancellationTokenSource();
        StandingLeasePreviewOwner? preview = null;
        void Choose(StandingLeaseApprovalResult result)
        {
            if (!decision.TrySetResult(result)) return;
            trace.Log("decision", result.Reason);
            try { close.Cancel(); } catch (ObjectDisposedException) { }
        }
        var registration = cancellationToken.Register(() => Choose(new(StandingLeaseApprovalOutcome.Cancelled, "lease_approval_cancelled")));
        var displayTimer = new System.Threading.Timer(_ => Choose(new(StandingLeaseApprovalOutcome.Unavailable,
            "lease_approval_display_timeout")), null, options.DisplayBudget, Timeout.InfiniteTimeSpan);
        var approvalTimer = new System.Threading.Timer(_ => Choose(new(StandingLeaseApprovalOutcome.TimedOut,
            "lease_approval_timed_out")), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        trace.Log("queued");
        var thread = new Thread(() =>
        {
            trace.Log("sta_started");
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
                if (close.IsCancellationRequested) return;
                trace.Log("construct_begin");
                options.BeforeConstructForTests?.Invoke();
                if (close.IsCancellationRequested) return;
                using var form = new StandingLeaseApprovalForm(details, options.PreviewProvider, text);
                trace.Log("construct_complete");
                var result = form.ShowOwnedModal(trace, options, close.Token, owner => preview = owner,
                    () =>
                    {
                        displayTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                        approvalTimer.Change(options.ApprovalBudget, Timeout.InfiniteTimeSpan);
                    });
                Choose(result);
            }
            catch { Choose(new(StandingLeaseApprovalOutcome.Unavailable, "lease_approval_initialization_failed")); }
            finally { trace.Log("sta_returning"); }
        }) { IsBackground = true, Name = "StandingLeaseApproval" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); }
        catch
        {
            Choose(new(StandingLeaseApprovalOutcome.Unavailable, "lease_approval_thread_unavailable"));
            registration.Dispose(); displayTimer.Dispose(); approvalTimer.Dispose(); close.Dispose();
            return new(decision.Task, Task.CompletedTask);
        }
        // A separate observer joins the actual threads; neither a public terminal
        // state nor a deadline may free the coordinator's UI gate early.
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _ = Task.Factory.StartNew(() =>
            {
                try
                {
                    thread.Join();
                    if (preview is not null) { trace.Log("retirement_waiting", "lease_preview_retirement_pending"); preview.Join(); }
                    registration.Dispose(); displayTimer.Dispose(); approvalTimer.Dispose(); close.Dispose();
                    trace.Log("retired");
                    retired.TrySetResult();
                }
                catch { trace.Log("retirement_unknown", "lease_approval_retirement_unknown"); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            Choose(new(StandingLeaseApprovalOutcome.Unavailable, "lease_approval_retirement_unknown"));
            trace.Log("retirement_unknown", "lease_approval_retirement_unknown");
        }
        return new(decision.Task, retired.Task);
    }
}
