using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRecorder.Infrastructure;
using Microsoft.Win32.SafeHandles;

namespace AgentRecorder.Mcp;

internal sealed class BoundHost : IDisposable
{
    private readonly Binding binding;
    private readonly Process process;
    private readonly string key;
    private readonly string readyPath;
    public HostStamp Stamp { get; }
    private BoundHost(Binding binding, Process process, HostStamp stamp, string key, string readyPath)
    { this.binding = binding; this.process = process; Stamp = stamp; this.key = key; this.readyPath = readyPath; }

    public static async Task<BoundHost> OpenAsync(Binding binding, HostStamp? expected, CancellationToken token)
    {
        BindingStore.Validate(binding);
        var caller = InteractiveDesktopRuntime.ObserveCurrent();
        if (!caller.IsOnInteractiveDesktop) throw new ContractException("interactive_desktop_required");
        var path = LocalPaths.Normalize(Path.Combine(binding.DataDir, "runtime", "ready.json"));
        var ready = ReadReady(path);
        var keyPath = LocalPaths.Normalize(Path.Combine(binding.DataDir, "config", "api-key.txt"));
        if (!ready.Ready || ready.Mode != "tray" || ready.ApiVersion != "v1" || ready.Port is < 1 or > 65535 ||
            !Guid.TryParseExact(ready.InstanceId, "D", out _) || ready.ProcessStartedAtUtcTicks <= 0 ||
            !LocalPaths.Equal(ready.DataDir, binding.DataDir) || !LocalPaths.Equal(ready.ReadyFile, path) ||
            !LocalPaths.Equal(ready.ApiKeyFile, keyPath) || !LocalPaths.Equal(ready.ExecutablePath, binding.AppPath) ||
            ready.ProcessUserSid != caller.ProcessUserSid || !InteractiveDesktopRuntime.IsReadySnapshotForActiveUser(ready, caller))
            throw new ContractException("ready_identity_unverified");
        var stamp = new HostStamp(ready.Pid, ready.Port, ready.InstanceId, ready.ProcessStartedAtUtcTicks, ready.ExecutablePath, ready.ProcessUserSid);
        if (expected != null && expected != stamp) throw new ContractException("operation_host_changed");
        var process = Process.GetProcessById(stamp.Pid);
        BoundHost? host = null;
        try
        {
            _ = process.Handle; // Hold the proven generation; no PID reuse or process cleanup ownership.
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != stamp.StartedTicks ||
                !LocalPaths.Equal(process.MainModule?.FileName ?? "", binding.AppPath) || ProcessSid(process.Handle) != stamp.UserSid)
                throw new ContractException("process_identity_unverified");
            // An unauthenticated, peer-owned capabilities read precedes any key read or transmission.
            using var capsClient = BoundLoopback.CreateClient(stamp.Port, stamp.Pid, () => !process.HasExited, singleConnection: true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "capabilities"); request.Headers.ConnectionClose = true;
            var caps = await ReadAsync(capsClient, request, TimeSpan.FromSeconds(5), token);
            if (caps.HttpStatus != 200 || !caps.Json.GetProperty("ok").GetBoolean()) throw new ContractException("capabilities_unavailable");
            var data = caps.Json.GetProperty("data");
            var identity = data.GetProperty("readiness");
            var snapshot = identity.Deserialize<ReadySnapshot>(EvidenceOptions)!;
            if (StampOf(snapshot) != stamp || !snapshot.Ready || snapshot.Mode != "tray" || snapshot.ApiVersion != "v1" ||
                !LocalPaths.Equal(snapshot.DataDir, binding.DataDir) || !LocalPaths.Equal(snapshot.ReadyFile, path) ||
                !LocalPaths.Equal(snapshot.ApiKeyFile, keyPath) ||
                !identity.GetProperty("interactive_desktop").GetProperty("ready").GetBoolean() ||
                data.GetProperty("app").GetProperty("version").GetString()?.Split('+')[0] != PackageValidator.SupportedVersion ||
                !data.GetProperty("host").GetProperty("supports_region_selection_ui").GetBoolean() ||
                !data.GetProperty("interaction").GetProperty("quick_recording_supported").GetBoolean() ||
                data.GetProperty("interaction").GetProperty("quick_recording_endpoint").GetString() != "/api/v1/recordings/quick" ||
                StampOf(ReadReady(path)) != stamp)
                throw new ContractException("capabilities_identity_unverified");
            var key = await ReadKeyAsync(keyPath, token);
            host = new(binding, process, stamp, key, path);
            return host;
        }
        catch { host?.Dispose(); if (host == null) process.Dispose(); throw; }
    }

    public async Task<(int HttpStatus, JsonElement Json)> SendAsync(HttpMethod method, string route, string? body, CancellationToken token,
        TimeSpan? observationTimeout = null)
    {
        BindingStore.Validate(binding);
        if (process.HasExited || StampOf(ReadReady(readyPath)) != Stamp) throw new ContractException("operation_host_changed");
        using var client = BoundLoopback.CreateClient(Stamp.Port, Stamp.Pid, () => !process.HasExited, singleConnection: true);
        using var request = new HttpRequestMessage(method, route);
        request.Headers.ConnectionClose = true;
        request.Headers.Add("X-Agent-Recorder-Key", key);
        request.Headers.Add("X-Agent-Recorder-Instance", Stamp.InstanceId);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await ReadAsync(client, request, observationTimeout ?? (method == HttpMethod.Post ? TimeSpan.FromSeconds(130) : TimeSpan.FromSeconds(5)), token);
    }

    private static async Task<(int HttpStatus, JsonElement Json)> ReadAsync(HttpClient client, HttpRequestMessage request, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if ((int)response.StatusCode is >= 300 and < 400) throw new ContractException("http_redirect_rejected");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[65537]; var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), deadline.Token);
            if (read == 0) break;
            count += read;
        }
        if (count > 65536) throw new ContractException("http_response_limit");
        using var doc = Json.Read(new UTF8Encoding(false, true).GetString(buffer, 0, count));
        return ((int)response.StatusCode, doc.RootElement.Clone());
    }

    private static readonly JsonSerializerOptions EvidenceOptions = new(Json.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
    private static ReadySnapshot ReadReady(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 16384) throw new ContractException("ready_identity_unverified");
        using var json = Json.Read(File.ReadAllText(path));
        return json.RootElement.Deserialize<ReadySnapshot>(EvidenceOptions) ?? throw new ContractException("ready_identity_unverified");
    }
    private static HostStamp StampOf(ReadySnapshot snapshot) => new(snapshot.Pid, snapshot.Port, snapshot.InstanceId,
        snapshot.ProcessStartedAtUtcTicks, snapshot.ExecutablePath, snapshot.ProcessUserSid);

    private static async Task<string> ReadKeyAsync(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var final = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(file.SafeFileHandle, final, final.Capacity, 0);
        if (length == 0 || length >= final.Capacity || !string.Equals(final.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new ContractException("key_path_unverified");
        var bytes = new byte[257]; int total = 0;
        while (total < bytes.Length)
        {
            int read = await file.ReadAsync(bytes.AsMemory(total), token); if (read == 0) break; total += read;
        }
        if (total > 256) throw new ContractException("key_unavailable");
        var value = new UTF8Encoding(false, true).GetString(bytes, 0, total).Trim();
        if (value.Length == 0 || value.Any(c => c is < '!' or > '~')) throw new ContractException("key_unavailable");
        return value;
    }

    private static string ProcessSid(IntPtr handle)
    {
        if (!OpenProcessToken(handle, 8, out var token)) throw new ContractException("process_identity_unverified");
        using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.Value ?? "";
    }
    public void Dispose() => process.Dispose();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr handle, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, int capacity, uint flags);
}
