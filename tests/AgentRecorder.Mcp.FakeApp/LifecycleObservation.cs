using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AgentRecorder.LifecycleTests;

// Test-only read-only probes. NULL Job queries below are strictly SELF queries;
// for another process only membership is observed, never the caller's limits.
internal sealed record ProcessObservation(int Pid, long? StartedTicks, string? Image, string Stage,
    DateTimeOffset ObservedAtUtc, bool? InAnyJob, int? MembershipError, uint? SelfImmediateJobLimitFlags,
    int? LimitsError, string JobLimitScope, int? LauncherPid, string OsParentStatus,
    uint? StdinKind, uint? StdoutKind, uint? StderrKind, string IdentityStatus);

internal static class LifecycleObservation
{
    public static ProcessObservation Self(string stage, int? launcherPid = null)
    { using var self = Process.GetCurrentProcess(); return Capture(self, stage, launcherPid); }

    public static ProcessObservation Capture(Process process, string stage, int? launcherPid = null)
    {
        bool? inJob = null; int? memberError = null, limitError = null; uint? flags = null;
        long? started = null; string? image = null; var identity = "unknown";
        var self = process.Id == Environment.ProcessId;
        try
        {
            _ = process.Handle;
            started = process.StartTime.ToUniversalTime().Ticks;
            image = Image(process.Handle); identity = "held_process_generation_and_image";
            if (IsProcessInJob(process.Handle, IntPtr.Zero, out var member)) inJob = member;
            else memberError = Marshal.GetLastWin32Error();
            if (self && inJob == true)
            {
                if (QueryInformationJobObject(IntPtr.Zero, 9, out var limits, Marshal.SizeOf<ExtendedLimits>(), IntPtr.Zero)) flags = limits.Basic.LimitFlags;
                else limitError = Marshal.GetLastWin32Error();
            }
        }
        catch { identity = "unknown_process_access_or_retired"; }
        return new(process.Id, started, image, stage, DateTimeOffset.UtcNow, inJob, memberError, flags, limitError,
            self ? "calling_process_immediate_job_only_ancestors_unknown" : "target_limits_unknown_no_job_handle",
            launcherPid, "not_queried_launcher_relation_is_not_os_ppid_proof",
            self ? FileKind(-10) : null, self ? FileKind(-11) : null, self ? FileKind(-12) : null, identity);
    }

    public static void WriteSelf(string directory, string name, int? launcherPid = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Self(name, launcherPid));
        var path = Path.Combine(directory, name + ".json");
        File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
    }
    public static string Image(IntPtr process)
    {
        var buffer = new System.Text.StringBuilder(32768); var count = buffer.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, buffer, ref count)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }
    private static uint? FileKind(int handle) { var value = GetFileType(GetStdHandle(handle)); return value == 0 ? null : value; }

    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits
    {
        public long PerProcessTime, PerJobTime; public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryInformationJobObject(IntPtr job, int informationClass, out ExtendedLimits information, int length, IntPtr returnLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(IntPtr handle);
}
