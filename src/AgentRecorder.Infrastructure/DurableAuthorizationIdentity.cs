using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Infrastructure;

internal sealed record DurableIdentityObservation(string? Binding, string? Reason)
{
    internal static DurableIdentityObservation Unavailable(string reason) => new(null, reason);
}

/// <summary>One bounded native query owner. A timed-out query retains its slot until it really exits.</summary>
internal sealed class DurableAuthorizationIdentity(Func<DurableIdentityObservation> source)
{
    private readonly object _gate = new();
    private QueryOwner? _inFlight;
    private sealed class QueryOwner(Task<DurableIdentityObservation> task)
    {
        internal Task<DurableIdentityObservation> Task { get; } = task;
        internal long StartedAt { get; } = Stopwatch.GetTimestamp();
        internal int TimedOut;
    }
    internal static readonly DurableAuthorizationIdentity Windows = new(ReadWindows);
    internal static readonly TimeSpan QueryTimeout = TimeSpan.FromMilliseconds(250);

    internal DurableIdentityObservation Read(TimeSpan? timeoutForTests = null)
    {
        QueryOwner query;
        lock (_gate)
        {
            if (_inFlight is { Task.IsCompleted: false } pending)
            {
                if (Volatile.Read(ref pending.TimedOut) != 0)
                    return DurableIdentityObservation.Unavailable("authorization_identity_busy");
                query = pending;
            }
            else
            {
                // Concurrent callers share only a live, unexpired query, never
                // a cached success or a timed-out query's late result.
                // The dedicated owner cannot queue behind blocked pool callers.
                try
                {
                    query = _inFlight = new(Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            var observation = source();
                            return observation.Binding is null
                                ? DurableIdentityObservation.Unavailable(observation.Reason ?? "authorization_identity_fields_invalid")
                                : IsCanonical(observation.Binding) ? new(observation.Binding, null)
                                : DurableIdentityObservation.Unavailable("authorization_identity_fields_invalid");
                        }
                        catch { return DurableIdentityObservation.Unavailable("authorization_identity_unavailable"); }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }
                catch { return DurableIdentityObservation.Unavailable("authorization_identity_unavailable"); }
            }
        }
        var timeout = timeoutForTests is { } shorter && shorter > TimeSpan.Zero && shorter < QueryTimeout
            ? shorter : QueryTimeout;
        try
        {
            var remaining = QueryTimeout - Stopwatch.GetElapsedTime(query.StartedAt);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            if (remaining < timeout) timeout = remaining;
            var result = query.Task.WaitAsync(timeout).GetAwaiter().GetResult();
            if (Stopwatch.GetElapsedTime(query.StartedAt) >= QueryTimeout) throw new TimeoutException();
            return Volatile.Read(ref query.TimedOut) == 0 ? result
                : DurableIdentityObservation.Unavailable("authorization_identity_timeout");
        }
        catch (TimeoutException)
        {
            Interlocked.Exchange(ref query.TimedOut, 1);
            return DurableIdentityObservation.Unavailable("authorization_identity_timeout");
        }
    }

    internal static string? Format(string? sid, uint bootSequence, ulong authenticationId, uint sessionId)
    {
        if (string.IsNullOrWhiteSpace(sid) || bootSequence == 0 || authenticationId == 0) return null;
        try { if (new SecurityIdentifier(sid).Value != sid) return null; }
        catch (ArgumentException) { return null; }
        return $"winlogon-v1|{sid}|{bootSequence:x8}|{authenticationId:x16}|{sessionId:x8}";
    }

    internal static bool IsCanonical(string? binding)
    {
        if (binding is null) return false;
        var parts = binding.Split('|');
        return parts.Length == 5 && parts[0] == "winlogon-v1" &&
            uint.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var boot) &&
            ulong.TryParse(parts[3], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var login) &&
            uint.TryParse(parts[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var session) &&
            Format(parts[1], boot, login, session) == binding;
    }

    private static DurableIdentityObservation ReadWindows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return DurableIdentityObservation.Unavailable("authorization_identity_platform_unsupported");
        // The documented Windows x64 KUSER_SHARED_DATA layout: NtBuildNumber at
        // 0x260, architecture at 0x26a, NtMajorVersion at 0x26c, BootId at 0x2c4.
        // ReadProcessMemory validates the readable mapping instead of dereferencing it.
        var shared = new byte[104];
        if (!ReadProcessMemory(GetCurrentProcess(), new IntPtr(0x7ffe0260), shared, (nuint)shared.Length, out var read) ||
            read != (nuint)shared.Length || BinaryPrimitives.ReadUInt16LittleEndian(shared.AsSpan(10)) != 9 ||
            BinaryPrimitives.ReadUInt32LittleEndian(shared.AsSpan(12)) != 10 ||
            BinaryPrimitives.ReadUInt32LittleEndian(shared) < 10240)
            return DurableIdentityObservation.Unavailable("authorization_boot_identity_unavailable");
        var boot = BinaryPrimitives.ReadUInt32LittleEndian(shared.AsSpan(100));
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out var token))
            return DurableIdentityObservation.Unavailable("authorization_token_unavailable");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var statistics = new byte[56];
            var session = new byte[4];
            if (!GetTokenInformation(token, 10, statistics, statistics.Length, out var statisticsLength) || statisticsLength != 56 ||
                !GetTokenInformation(token, 12, session, session.Length, out var sessionLength) || sessionLength != 4)
                return DurableIdentityObservation.Unavailable("authorization_token_unavailable");
            var binding = Format(identity.User?.Value, boot,
                BinaryPrimitives.ReadUInt64LittleEndian(statistics.AsSpan(8)), BinaryPrimitives.ReadUInt32LittleEndian(session));
            return binding is null ? DurableIdentityObservation.Unavailable("authorization_identity_fields_invalid") : new(binding, null);
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, nuint length, out nuint read);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        [Out] byte[] information, int length, out int returnLength);
}
