using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.App;

internal sealed class TrayRecurringPlanSetupUi(TrayContext tray) : IRecurringPlanSetupUi
{
    public bool IsInteractiveDesktopAvailable => tray.SupportsRegionSelectionUi && InteractiveDesktopAvailable();

    public Task<RecurringPlanSetupSelection> SelectFreshRegionAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new RecurringPlanSetupSelection(RecurringRegionSelectionStatus.HostShutdown));
        if (!IsInteractiveDesktopAvailable)
            return Task.FromResult(new RecurringPlanSetupSelection(RecurringRegionSelectionStatus.Unavailable));
        var completion = new TaskCompletionSource<RecurringPlanSetupSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        tray.RequestRecurringRegionSelection(300, (status, x, y, width, height, _, coordinateSpace) =>
            completion.TrySetResult(new(MapSelectionStatus(status), x, y, width, height, coordinateSpace)), cancellationToken);
        return completion.Task;
    }

    internal static RecurringRegionSelectionStatus MapSelectionStatus(string status) => status switch
    {
        "selected" => RecurringRegionSelectionStatus.Selected,
        "selection_cancelled" => RecurringRegionSelectionStatus.Cancelled,
        "selection_timeout" => RecurringRegionSelectionStatus.TimedOut,
        "host_shutdown" => RecurringRegionSelectionStatus.HostShutdown,
        "display_unavailable" => RecurringRegionSelectionStatus.Invalid,
        "error" => RecurringRegionSelectionStatus.Unavailable,
        _ => RecurringRegionSelectionStatus.Conflict,
    };

    public Task<RecurringLeaseApprovalResult> ShowApprovalAsync(RecurringLeaseApprovalDetails details, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromResult(RecurringLeaseApprovalResult.HostShutdown);
        if (!IsInteractiveDesktopAvailable) return Task.FromResult(RecurringLeaseApprovalResult.Unavailable);
        var completion = new TaskCompletionSource<RecurringLeaseApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(RecurringLeaseApprovalForm.ShowModal(details, tray.CurrentUiTextProvider, cancellationToken)); }
            catch { completion.TrySetResult(cancellationToken.IsCancellationRequested ? RecurringLeaseApprovalResult.HostShutdown : RecurringLeaseApprovalResult.Unavailable); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static bool InteractiveDesktopAvailable()
    {
        nint input = 0;
        try
        {
            if (!Environment.UserInteractive) return false;
            input = OpenInputDesktop(0, false, 1);
            var current = GetThreadDesktop(GetCurrentThreadId());
            return input != 0 && DesktopName(input) == "Default" && DesktopName(current) == "Default";
        }
        catch { return false; }
        finally { if (input != 0) CloseDesktop(input); }
    }

    private static string? DesktopName(nint desktop)
    {
        var name = new StringBuilder(256);
        return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ? name.ToString() : null;
    }
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder info, int length, out int needed);
}
