using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Cli;

internal static partial class Program
{
    // Read-only gate. Native Setup/Uninstall owns BOTH mutexes for the entire mutation lifetime.
    // A successful standalone invocation is not a lease or authorization to modify anything.
    private static int RunInstallationCheck(string[] args)
    {
        string? root = null, operation = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--program-root" && root is null && i + 1 < args.Length) root = args[++i];
            else if (args[i] == "--operation" && operation is null && i + 1 < args.Length) operation = args[++i];
            else { Console.Error.WriteLine("INSTALL_ARGUMENT_INVALID"); return 1; }
        }
        var result = EvaluateInstallationSafety(root, operation, new InstallationSafetyChecks());
        Console.WriteLine(JsonSerializer.Serialize(result));
        if (!result.Safe) Console.Error.WriteLine(result.Code);
        return result.Safe ? 0 : 1;
    }

    internal sealed record InstallationSafetyResult(bool Safe, string Code);
    internal sealed class InstallationSafetyChecks
    {
        public string LocalRoot { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public Func<bool> MutexesPresent { get; init; } = () =>
            IsMutexHeld(SingleInstanceGuard.MutexName) && IsMutexHeld(InstallationContext.MaintenanceMutexName);
        public Func<string, bool> NoHostProcesses { get; init; } = root => NoInstallationHostProcesses(
            ObserveInstallationHosts(), WindowsIdentity.GetCurrent().User?.Value ?? "", root);
        public Func<bool> PortAbsent { get; init; } = () =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, ApiPort));
                return true;
            }
            catch (SocketException) { return false; }
        };
        public Func<string, bool> NoIntegrations { get; init; } = NoInstallationIntegrations;
        public Func<int, InstallationPidState> ObserveReadyPid { get; init; } = ObserveInstallationReadyPid;
        public Func<bool> NoInstallationRegistration { get; init; } = NoReadyInstallationRegistration;
        public Func<int, string, string[], string[]?> ReadInstallationRegistration { get; init; } = ReadReadyInstallationRegistration;
        public Func<string?> CurrentUserSid { get; init; } = () =>
            WindowsIdentity.GetCurrent().User?.Value;
    }

    internal static InstallationSafetyResult EvaluateInstallationSafety(string? root, string? operation, InstallationSafetyChecks checks)
    {
        try
        {
            if (operation is not ("install" or "upgrade" or "uninstall") || string.IsNullOrWhiteSpace(root) ||
                !Path.IsPathFullyQualified(root) || !InstallationContext.Equal(root, InstallationContext.ExpectedProgramRoot(checks.LocalRoot)))
                return new(false, "INSTALL_ARGUMENT_INVALID");
            InstallationContext.RequireNonReparse(root);
            RequireOrdinaryInstallationTree(root);
            var data = Path.Combine(checks.LocalRoot, "AgentRecorder");
            InstallationContext.RequireSeparate(root, data);
            var installed = InstallationContext.IsInstalled(root, checks.LocalRoot);
            if (!installed && (operation != "install" ||
                (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())))
                return new(false, "INSTALL_OWNER_UNKNOWN");
            if (!checks.MutexesPresent()) return new(false, "INSTALL_LEASE_MISSING");
            if (!checks.NoHostProcesses(root)) return new(false, "INSTALL_HOST_ACTIVE_OR_UNKNOWN");
            return CheckInstallationReady(root, data, operation == "install",
                installed && (operation is "upgrade" or "uninstall"), checks);
        }
        catch { return new(false, "INSTALL_STATE_UNKNOWN"); }
    }

    internal static void RequireOrdinaryInstallationTree(string root)
    {
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("INSTALL_REPARSE_PATH");
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
        }
    }

    private static IEnumerable<IShutdownProcess> ObserveInstallationHosts()
    {
        foreach (var name in new[] { "AgentRecorder.App", "AgentRecorder.Headless", "AgentRecorder.Cli", "AgentRecorder.AudioHelper", "wgc-native-helper" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
            yield return new ShutdownProcess(process); // The safe-shutdown OS-handle identity adapter; never process-name-only proof.
        }
    }

    internal static bool NoInstallationHostProcesses(IEnumerable<IShutdownProcess> candidates, string sid, string root)
    {
        if (string.IsNullOrEmpty(sid)) return false;
        foreach (var candidate in candidates)
        using (candidate)
        {
            if (candidate.HasExited) continue;
            var owner = candidate.ProcessUserSid;
            var image = candidate.ExecutablePath;
            if (string.IsNullOrEmpty(owner) || !Path.IsPathFullyQualified(image) || candidate.StartedAtUtcTicks <= 0) return false;
            // Same-user other package/session or any process actually using this installation is a conflict.
            if (owner == sid || InstallationContext.Contains(root, image)) return false;
        }
        return true;
    }

    private static bool NoInstallationIntegrations(string root)
        => NoInstallationIntegrations(root, new RegistryRunKey(), InteractiveLaunchRegistrationStore.RegistrationPath);

    internal static bool NoInstallationIntegrations(string root, IRegistryRunKey runKey, string registrationPath)
    {
        // Do not guess aliases/arguments/ownership of any existing product Run entry.
        if (runKey.ValueExists(WindowsAutoStartManager.DefaultValueName)) return false;
        try { _ = File.GetAttributes(registrationPath); }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        InstallationContext.RequireNonReparse(registrationPath);
        if (!InteractiveLaunchRegistrationStore.TryLoad(registrationPath, out var registration, out _) || registration is null) return false;
        // Do not remove tasks/Run entries even when apparently ours: installer has no integration cleanup authority.
        return !InstallationContext.Contains(root, registration.AppPath);
    }
}
