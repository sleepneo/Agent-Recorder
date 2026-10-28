using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Cli;

internal static partial class Program
{
    internal sealed record ShutdownResult(bool Ok, string Status, string ReasonCode, int? Pid = null);
    internal interface IShutdownProcess : IDisposable
    {
        long StartedAtUtcTicks { get; }
        string ExecutablePath { get; }
        string ProcessUserSid { get; }
        bool HasExited { get; }
        Task WaitForExitAsync(CancellationToken cancellation);
    }
    private sealed class ShutdownProcess(Process process) : IShutdownProcess
    {
        // Force an OS process handle now, before POST: wait observes this process, never a reused PID.
        private readonly IntPtr _handle = process.Handle;
        public long StartedAtUtcTicks => process.StartTime.ToUniversalTime().Ticks;
        public string ExecutablePath => process.MainModule?.FileName ?? "";
        public string ProcessUserSid
        {
            get
            {
                if (!OpenProcessToken(_handle, 0x0008, out var token)) throw new System.ComponentModel.Win32Exception();
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    return identity.User?.Value ?? "";
            }
        }
        public bool HasExited => process.HasExited;
        public Task WaitForExitAsync(CancellationToken cancellation) => process.WaitForExitAsync(cancellation);
        public void Dispose() => process.Dispose();
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    }

    private static int RunShutdown(string[] args)
    {
        bool json = args.Contains("--json");
        string? dataDir = null;
        int timeout = 15000;
        string? error = null;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json": break;
                case "--data-dir" when i + 1 < args.Length: dataDir = args[++i]; break;
                case "--timeout-ms" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out timeout) || timeout is < 100 or > 120000) error = "invalid_timeout";
                    break;
                case "--help": case "-h":
                    Console.WriteLine("shutdown --json --data-dir <directory> [--timeout-ms 15000]");
                    Console.WriteLine("Authenticated idle-only exit. Never starts, kills, cancels work, or revokes saved plans. accepted is not exited.");
                    return 0;
                default: error = "invalid_option"; break;
            }
        }
        ShutdownResult result;
        if (error is not null) result = new(false, "invalid_request", error);
        else
        {
            try
            {
                dataDir ??= ResolveDataDir(new CliOptions(), ResolvePackageRoot(new CliOptions()));
                using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
                    { Timeout = Timeout.InfiniteTimeSpan };
                result = ShutdownCoreAsync(dataDir, timeout, http,
                    pid => { try { return new ShutdownProcess(Process.GetProcessById(pid)); } catch (ArgumentException) { return null; } },
                    WindowsIdentity.GetCurrent().User?.Value ?? "", () => IsMutexHeld(SingleInstanceGuard.MutexName))
                    .GetAwaiter().GetResult();
            }
            catch { result = new(false, "unknown", "identity_unavailable"); }
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        else Console.WriteLine($"{result.Status}: {result.ReasonCode}");
        return result.Ok ? 0 : 1;
    }

    internal static async Task<ShutdownResult> ShutdownCoreAsync(string directory, int timeoutMs, HttpClient http,
        Func<int, IShutdownProcess?> openProcess, string currentUserSid, Func<bool> instanceHeld)
    {
        int? pid = null;
        bool accepted = false;
        using var budget = new CancellationTokenSource(timeoutMs);
        try
        {
            var root = Path.GetFullPath(directory);
            var readyPath = Path.Combine(root, "runtime", "ready.json");
            if (!File.Exists(readyPath)) return instanceHeld()
                ? new(false, "unknown", "wrong_data_dir_or_unready") : new(true, "not_running", "ready_absent");
            var ready = ReadReadySnapshot(readyPath);
            if (ready is null || !ready.Ready || ready.Pid <= 0 || ready.Port is < 1 or > 65535 ||
                !PathsEqual(ready.DataDir, root) || !PathsEqual(ready.ReadyFile, readyPath) ||
                !PathsEqual(ready.ApiKeyFile, Path.Combine(root, "config", "api-key.txt")))
                return new(false, "unknown", "stale_ready_or_wrong_data_dir");
            pid = ready.Pid;
            using var process = openProcess(ready.Pid);
            if (process is null || process.HasExited) return new(false, "unknown", "stale_ready", pid);

            var endpoint = $"http://127.0.0.1:{ready.Port}/api/v1/";
            using var capsResponse = await http.GetAsync(endpoint + "capabilities", budget.Token);
            if (!capsResponse.IsSuccessStatusCode) return new(false, "unknown", "capabilities_unavailable", pid);
            string caps = await capsResponse.Content.ReadAsStringAsync(budget.Token);
            if (!ValidateReadySnapshotAgainstCapabilitiesJson(ready, caps).Valid)
                return new(false, "unknown", "ready_identity_mismatch", pid);
            using var doc = JsonDocument.Parse(caps);
            var data = doc.RootElement.GetProperty("data");
            if (!data.TryGetProperty("host_shutdown", out var support) ||
                !support.TryGetProperty("supported", out var supported) || !supported.GetBoolean())
                return new(false, "unsupported", "host_shutdown_unsupported", pid);
            if (support.GetProperty("endpoint").GetString() != "/api/v1/host/shutdown" ||
                support.GetProperty("method").GetString() != "POST" || !support.GetProperty("idle_only").GetBoolean() || support.GetProperty("force").GetBoolean())
                return new(false, "unsupported", "shutdown_contract_mismatch", pid);
            var identity = data.GetProperty("readiness");
            if (!Guid.TryParseExact(ready.InstanceId, "D", out _) || ready.ProcessStartedAtUtcTicks <= 0 ||
                string.IsNullOrEmpty(currentUserSid) || ready.ProcessUserSid != currentUserSid ||
                identity.GetProperty("instance_id").GetString() != ready.InstanceId ||
                identity.GetProperty("process_started_at_utc_ticks").GetInt64() != ready.ProcessStartedAtUtcTicks ||
                identity.GetProperty("process_user_sid").GetString() != currentUserSid ||
                !PathsEqual(identity.GetProperty("executable_path").GetString() ?? "", ready.ExecutablePath) ||
                !PathsEqual(process.ExecutablePath, ready.ExecutablePath) ||
                process.ProcessUserSid != currentUserSid ||
                process.StartedAtUtcTicks != ready.ProcessStartedAtUtcTicks ||
                Path.GetFileName(ready.ExecutablePath) is not ("AgentRecorder.App.exe" or "AgentRecorder.Headless.exe"))
                return new(false, "unknown", "process_identity_mismatch", pid);
            // Reread immediately before POST, including the nonce; never rewrite/delete ready from shutdown.
            var latest = ReadReadySnapshot(readyPath);
            if (latest?.InstanceId != ready.InstanceId || latest.Pid != ready.Pid || process.HasExited)
                return new(false, "unknown", "ready_changed", pid);
            var key = (await File.ReadAllTextAsync(ready.ApiKeyFile, budget.Token)).Trim();
            if (string.IsNullOrEmpty(key)) return new(false, "unknown", "key_unavailable", pid);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint + "host/shutdown");
            request.Headers.Add("X-Agent-Recorder-Key", key);
            request.Headers.Add("X-Agent-Recorder-Instance", ready.InstanceId);
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, budget.Token);
            if (response.StatusCode == HttpStatusCode.NotFound || (int)response.StatusCode == 501)
                return new(false, "unsupported", "host_shutdown_unsupported", pid);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(false, "unknown", "authentication_failed", pid);
            var responseText = await response.Content.ReadAsStringAsync(budget.Token);
            using var result = JsonDocument.Parse(responseText);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var error = result.RootElement.GetProperty("error");
                if (error.GetProperty("code").GetString() != "HOST_BUSY")
                    return new(false, "unknown", "host_identity_or_admission_changed", pid);
                return new(false, "busy", error.GetProperty("details").GetProperty("reason_code").GetString() ?? "host_busy", pid);
            }
            if ((int)response.StatusCode != 202 || !result.RootElement.GetProperty("ok").GetBoolean() ||
                result.RootElement.GetProperty("data").GetProperty("status").GetString() != "accepted")
                return new(false, "unknown", "shutdown_not_accepted", pid);
            accepted = true;
            await process.WaitForExitAsync(budget.Token);
            return process.HasExited ? new(true, "exited", "bound_process_exited", pid)
                : new(false, "unknown", "exit_unconfirmed", pid);
        }
        catch (OperationCanceledException) { return new(false, "timeout", accepted ? "accepted_exit_unconfirmed" : "shutdown_deadline", pid); }
        catch { return new(false, "unknown", accepted ? "accepted_exit_unknown" : "transport_or_identity_unknown", pid); }
    }
}
