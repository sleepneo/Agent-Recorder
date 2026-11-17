using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentRecorder.LifecycleTests;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Mcp.Tests;

// Test-only, unnamed, non-inheritable Job. Only a freshly CreateProcess'd fake
// CLI in this fixture can be assigned. Never accepts an arbitrary existing PID.
internal sealed class DedicatedSyntheticJob : IDisposable
{
    private readonly SafeJob job;
    private readonly string root;
    public uint ConfiguredFlags { get; }
    public ProcessObservation? AssignedBeforeResume { get; private set; }
    public ProcessObservation? CreatedBeforeAssignment { get; private set; }
    public string? SetupAbortRetirement { get; private set; }
    public DedicatedSyntheticJob(string fixtureRoot, bool killOnClose)
    {
        root = Path.GetFullPath(fixtureRoot) + Path.DirectorySeparatorChar;
        job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw NativeFailure("create_job");
        ConfiguredFlags = killOnClose ? 0x2000u : 0;
        var limits = new LifecycleObservation.ExtendedLimits { Basic = new() { LimitFlags = ConfiguredFlags } };
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<LifecycleObservation.ExtendedLimits>()))
        { var error = NativeFailure("set_job_limits"); job.Dispose(); throw error; }
    }
    public uint ReadOwnedLimitFlags()
    {
        if (!LifecycleObservation.QueryInformationJobObject(job.DangerousGetHandle(), 9, out var info,
            Marshal.SizeOf<LifecycleObservation.ExtendedLimits>(), IntPtr.Zero)) throw NativeFailure("query_owned_job");
        return info.Basic.LimitFlags;
    }
    public bool ContainsVerifiedFake(Process process, string expectedImage, long expectedTicks)
    {
        Verify(process, expectedImage, expectedTicks);
        if (!LifecycleObservation.IsProcessInJob(process.Handle, job.DangerousGetHandle(), out var member)) throw NativeFailure("owned_membership");
        return member;
    }
    public Process CreateAssignedFakeCli(string cli, string app, string data, string control)
    {
        if (!IsOwned(cli) || !IsOwned(app) || !IsOwned(data) || !IsOwned(control) ||
            Path.GetFileName(cli) != "AgentRecorder.Cli.exe" || Path.GetFileName(app) != "AgentRecorder.App.exe")
            throw new InvalidOperationException("not_a_fixture_fake");
        string Quote(string value) => value.Contains('"') || value.EndsWith('\\') ? throw new InvalidOperationException("unsafe_synthetic_path") : "\"" + value + "\"";
        var command = new StringBuilder(string.Join(" ", new[] { cli, "--lifecycle-job-child", "--app", app, "--data-dir", data, "--control", control }.Select(Quote)));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(cli, command, IntPtr.Zero, IntPtr.Zero, false, 0x00000004 | 0x08000000,
            IntPtr.Zero, Path.GetDirectoryName(cli), ref startup, out var created)) throw NativeFailure("create_suspended_fake");
        Process? process = null; bool resumed = false;
        try
        {
            process = Process.GetProcessById(created.ProcessId); _ = process.Handle;
            Verify(process, cli, process.StartTime.ToUniversalTime().Ticks);
            CreatedBeforeAssignment = LifecycleObservation.Capture(process, "owned_fake_created_before_assignment", Environment.ProcessId);
            if (!AssignProcessToJobObject(job, created.Process)) throw NativeFailure("assign_own_suspended_fake");
            if (!ContainsVerifiedFake(process, cli, process.StartTime.ToUniversalTime().Ticks)) throw new InvalidOperationException("assigned_membership_unproven");
            AssignedBeforeResume = LifecycleObservation.Capture(process, "owned_job_assigned_before_resume", Environment.ProcessId);
            if (ResumeThread(created.Thread) == uint.MaxValue) throw NativeFailure("resume_own_fake");
            resumed = true;
            return process;
        }
        catch
        {
            // Failed setup never counts as a model pass. Resume only our own
            // still-suspended fake after setting its pre-start normal abort signal.
            File.WriteAllText(Path.Combine(control, "abort"), "normal_abort");
            if (!resumed) ResumeThread(created.Thread);
            SetupAbortRetirement = process?.WaitForExit(8000) == true ? "normal_abort_retired" : "unknown_created_identity_retained";
            process?.Dispose();
            throw;
        }
        finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
    }
    private bool IsOwned(string path) => Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    private void Verify(Process process, string image, long ticks)
    {
        if (!IsOwned(image) || process.HasExited || process.StartTime.ToUniversalTime().Ticks != ticks ||
            !string.Equals(LifecycleObservation.Image(process.Handle), Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("fake_generation_or_image_unverified");
    }
    private static Exception NativeFailure(string code) => new InvalidOperationException(code + "_win32_" + Marshal.GetLastWin32Error());
    public void Dispose() => job.Dispose(); // Explicit model retirement; never a production cleanup hook.

    private sealed class SafeJob : SafeHandleZeroOrMinusOneIsInvalid
    { public SafeJob() : base(true) { } protected override bool ReleaseHandle() => CloseHandle(handle); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size; public IntPtr Reserved2, Stdin, Stdout, Stderr;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeJob CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJob job, int informationClass, ref LifecycleObservation.ExtendedLimits info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJob job, IntPtr process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation created);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
