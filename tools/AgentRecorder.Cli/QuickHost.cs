using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Cli;

internal static partial class Program
{
    internal sealed class QuickHost(HttpClient reads, HttpClient submission, string instance, string key,
        Func<int, Task<ShutdownResult>>? exit, IDisposable? process = null) : IDisposable
    {
        internal HttpClient Reads { get; } = reads;
        internal HttpClient Submission { get; } = submission;
        internal string Instance { get; } = instance;
        internal string Key { get; } = key;
        internal Func<int, Task<ShutdownResult>>? Exit { get; } = exit;
        public void Dispose()
        {
            Submission.Dispose();
            Reads.Dispose();
            process?.Dispose();
        }
    }

    internal sealed class QuickFailure(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }

    private static async Task<QuickHost> AcquireQuickHostAsync(CliOptions opts, CancellationToken token)
    {
        var desktop = InteractiveDesktopRuntime.ObserveCurrent();
        if (!desktop.IsOnInteractiveDesktop) throw new QuickFailure("interactive_desktop_required");
        var package = ResolvePackageRoot(opts);
        var root = Path.GetFullPath(ResolveDataDir(opts, package));
        if (root.Length < 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\' ||
            new DriveInfo(root[..3]).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new QuickFailure("local_data_directory_required");
        var exe = ResolveServiceExe(opts, package);
        if (string.IsNullOrEmpty(exe) || !string.Equals(Path.GetFileName(exe), "AgentRecorder.App.exe", StringComparison.OrdinalIgnoreCase))
            throw new QuickFailure("tray_app_not_found");

        var readyPath = Path.Combine(root, "runtime", "ready.json");
        var ready = ReadReadySnapshot(readyPath);
        if (ready is { Pid: > 0 } && IsAgentRecorderProcess(ready.Pid))
            return await BindQuickHostAsync(ready, readyPath, root, exe, desktop, false, token).ConfigureAwait(false);
        if (IsMutexHeld(SingleInstanceGuard.MutexName))
            throw new QuickFailure("existing_host_unverified");

        token.ThrowIfCancellationRequested();
        var launchId = InteractiveLaunchProtocol.NewId();
        var info = new ProcessStartInfo(Path.GetFullPath(exe), InteractiveLaunchProtocol.CreateDirectArguments(launchId))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["AGENT_RECORDER_DATA_DIR"] = root;
        using var child = Process.Start(info) ?? throw new QuickFailure("host_start_failed");
        child.StandardInput.Close();
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (InteractiveLaunchResultStore.TryReadAndDelete(root, launchId, out var launch) &&
                launch?.ProcessId == child.Id && launch.Status is "rejected" or "failed")
                throw new QuickFailure("host_start_failed");
            ready = ReadReadySnapshot(readyPath);
            if (ready is { Ready: true, Pid: > 0 })
            {
                if (ready.Pid == child.Id && !child.HasExited &&
                    ready.ProcessStartedAtUtcTicks == child.StartTime.ToUniversalTime().Ticks && PathsEqual(ready.ExecutablePath, exe))
                    return await BindQuickHostAsync(ready, readyPath, root, exe, desktop, true, token).ConfigureAwait(false);
                if (ready.Pid != child.Id && IsAgentRecorderProcess(ready.Pid))
                    return await BindQuickHostAsync(ready, readyPath, root, exe, desktop, false, token).ConfigureAwait(false);
            }
            if (child.HasExited) throw new QuickFailure("host_exited_before_ready");
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private static async Task<QuickHost> BindQuickHostAsync(ReadySnapshot ready, string readyPath, string root, string exe,
        InteractiveDesktopObservation desktop, bool owned, CancellationToken token)
    {
        var expectedKey = Path.Combine(root, "config", "api-key.txt");
        if (!ready.Ready || ready.Mode != "tray" || ready.Port != ApiPort ||
            !Guid.TryParseExact(ready.InstanceId, "D", out _) || ready.ProcessStartedAtUtcTicks <= 0 ||
            !PathsEqual(ready.DataDir, root) || !PathsEqual(ready.ReadyFile, readyPath) ||
            !PathsEqual(ready.ApiKeyFile, expectedKey) ||
            !PathsEqual(ready.ExecutablePath, exe) ||
            ready.ProcessUserSid != desktop.ProcessUserSid ||
            !InteractiveDesktopRuntime.IsReadySnapshotForActiveUser(ready, desktop))
            throw new QuickFailure("ready_identity_unverified");

        var process = new ShutdownProcess(Process.GetProcessById(ready.Pid));
        HttpClient? reads = null, submission = null;
        try
        {
            if (process.HasExited || process.StartedAtUtcTicks != ready.ProcessStartedAtUtcTicks ||
                !PathsEqual(process.ExecutablePath, ready.ExecutablePath) || process.ProcessUserSid != desktop.ProcessUserSid)
                throw new QuickFailure("process_identity_unverified");
            reads = CreateQuickClient(ready.Port, ready.Pid, () => !process.HasExited);
            using var caps = await reads.GetAsync("capabilities", token).ConfigureAwait(false);
            if (!caps.IsSuccessStatusCode) throw new QuickFailure("capabilities_unavailable");
            var json = await caps.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var identity = doc.RootElement.GetProperty("data").GetProperty("readiness");
            if (!ValidateReadySnapshotAgainstCapabilitiesJson(ready, json).Valid ||
                identity.GetProperty("instance_id").GetString() != ready.InstanceId ||
                identity.GetProperty("process_started_at_utc_ticks").GetInt64() != ready.ProcessStartedAtUtcTicks ||
                identity.GetProperty("process_user_sid").GetString() != ready.ProcessUserSid ||
                !PathsEqual(identity.GetProperty("executable_path").GetString() ?? "", ready.ExecutablePath) ||
                !SameQuickReady(ReadReadySnapshot(readyPath), ready))
                throw new QuickFailure("capabilities_identity_unverified");

            // Read only the expected, previously verified local key file. Never
            // project the key, server messages or raw transport errors to output.
            var key = await ReadBoundQuickKeyAsync(expectedKey, token).ConfigureAwait(false);
            if (key.Length == 0 || key.Any(c => c is < '!' or > '~'))
                throw new QuickFailure("key_unavailable");
            submission = CreateQuickClient(ready.Port, ready.Pid, () => !process.HasExited, singleConnection: true);
            return new QuickHost(reads, submission, ready.InstanceId, key, owned ? timeout =>
                ShutdownCoreAsync(root, timeout, reads,
                    pid => pid == ready.Pid && !process.HasExited ? new ShutdownProcess(Process.GetProcessById(pid)) : null,
                    desktop.ProcessUserSid, () => IsMutexHeld(SingleInstanceGuard.MutexName), ready, key) : null, process);
        }
        catch
        {
            submission?.Dispose();
            reads?.Dispose();
            process.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadBoundQuickKeyAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var final = new StringBuilder(WindowsReadOnlyFileMetadataProbe.FinalPathCapacity);
        var length = WindowsFileMetadataApi.Instance.FinalPath(file.SafeFileHandle, final, final.Capacity, out _);
        if (length == 0 || length >= final.Capacity || length != final.Length ||
            !string.Equals(final.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new QuickFailure("key_path_unverified");
        return (await ReadQuickTextAsync(file, 256, token).ConfigureAwait(false)).Trim();
    }

    private static bool SameQuickReady(ReadySnapshot? current, ReadySnapshot original) =>
        current is { Ready: true } && current.InstanceId == original.InstanceId && current.Pid == original.Pid &&
        current.Port == original.Port && current.ProcessStartedAtUtcTicks == original.ProcessStartedAtUtcTicks &&
        current.ProcessUserSid == original.ProcessUserSid && PathsEqual(current.ExecutablePath, original.ExecutablePath) &&
        PathsEqual(current.ReadyFile, original.ReadyFile) && PathsEqual(current.DataDir, original.DataDir) &&
        PathsEqual(current.ApiKeyFile, original.ApiKeyFile);
}
