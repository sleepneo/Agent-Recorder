using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

internal static class ApiFixture
{
    private const string Recording = "rec_123456abcdef";
    private const string Confirmation = "confirm_123456abcdef";
    private const string OtherRecording = "rec_abcdef123456";
    public static async Task<int> RunAsync(string[] args)
    {
        string Arg(string name) => args[Array.IndexOf(args, name) + 1];
        var data = Arg("--data-dir"); var control = Arg("--control"); var exit = Arg("--signal");
        Directory.CreateDirectory(control); Directory.CreateDirectory(Path.Combine(data, "runtime"));
        if (args.Contains("--lifecycle-observe")) AgentRecorder.LifecycleTests.LifecycleObservation.WriteSelf(control, "app-start",
            args.Contains("--lifecycle-launcher") ? int.Parse(Arg("--lifecycle-launcher")) : null);
        if (args.Contains("--lifecycle-stdin-sensitive")) _ = Task.Run(async () =>
        {
            await Console.In.ReadToEndAsync();
            File.WriteAllText(Path.Combine(control, "stdin-eof-observed"), "explicit_synthetic_policy");
            File.WriteAllText(exit, "stdin_eof_synthetic_policy");
        });
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var pid = Environment.ProcessId; var ticks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        var sid = WindowsIdentity.GetCurrent().User!.Value; var instance = Guid.NewGuid().ToString("D");
        var readyFile = Path.Combine(data, "runtime", "ready.json");
        var ready = new Dictionary<string, object?>
        {
            ["ready"] = true, ["mode"] = "tray", ["api_version"] = "v1", ["pid"] = pid, ["port"] = port,
            ["instance_id"] = instance, ["process_started_at_utc_ticks"] = ticks,
            ["process_user_sid"] = sid, ["executable_path"] = Environment.ProcessPath,
            ["data_dir"] = data, ["ready_file"] = readyFile, ["api_key_file"] = Path.Combine(data, "config", "api-key.txt"),
            ["interactive_desktop_ready"] = true, ["session_id"] = Process.GetCurrentProcess().SessionId,
            ["window_station"] = "WinSta0", ["desktop"] = "Default"
        };
        File.WriteAllText(readyFile, JsonSerializer.Serialize(ready));
        File.WriteAllText(Path.Combine(control, "ready"), pid.ToString());
        File.WriteAllText(Path.Combine(control, "other-state"), "recording");
        var tasks = new List<Task>();
        var duration = 10;
        var stop = Task.Run(async () => { while (!File.Exists(exit) && !lifetime.IsCancellationRequested) await Task.Delay(30); lifetime.Cancel(); listener.Stop(); });
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                tasks.Add(Handle(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (lifetime.IsCancellationRequested) { }
        await Task.WhenAll(tasks); await stop;
        File.WriteAllText(Path.Combine(control, "retired"), "normal");
        return 0;

        string Mode(string field) => File.Exists(Path.Combine(control, field)) ? File.ReadAllText(Path.Combine(control, field)) : "";
        async Task Handle(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                var line = await reader.ReadLineAsync(lifetime.Token); if (line == null) return;
                var first = line.Split(' '); var route = first[1]; int length = 0; bool keySeen = false;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(lifetime.Token)))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                    if (line.StartsWith("X-Agent-Recorder-Key:", StringComparison.OrdinalIgnoreCase)) keySeen = true;
                }
                lock (tasks)
                {
                    File.AppendAllText(Path.Combine(control, "routes"), first[0] + " " + route + "\n");
                    if (keySeen) File.AppendAllText(Path.Combine(control, "authenticated"), "yes\n");
                }
                if (route == "/api/v1/capabilities")
                {
                    var evidence = new Dictionary<string, object?>(ready) { ["interactive_desktop"] = new { ready = true } };
                    if (Mode("caps-mode") == "wrong-instance") evidence["instance_id"] = Guid.NewGuid().ToString("D");
                    if (Mode("caps-mode") == "wrong-pid") evidence["pid"] = pid + 1000;
                    if (Mode("caps-mode") == "redirect") { await Reply(302, new { }, "Location: http://127.0.0.1:1/steal\r\n"); return; }
                    await Reply(200, new { ok = true, data = new
                    { readiness = evidence, app = new { version = Mode("caps-mode") == "wrong-version" ? "0.1.26" : "0.1.25" },
                        host = new { supports_region_selection_ui = Mode("caps-mode") != "no-region" }, interaction = new { quick_recording_supported = true, quick_recording_endpoint = "/api/v1/recordings/quick" } } });
                    return;
                }
                if (!keySeen) { await Reply(401, new { ok = false, error = new { code = "UNAUTHORIZED" } }); return; }
                if (first[0] == "POST" && (route == "/api/v1/recordings/" + Recording + "/stop" || route == "/api/v1/recordings/" + OtherRecording + "/stop"))
                {
                    var target = route.Contains(OtherRecording) ? OtherRecording : Recording;
                    var body = new char[length]; if (await reader.ReadBlockAsync(body.AsMemory(), lifetime.Token) != length) return;
                    lock (tasks) File.AppendAllText(Path.Combine(control, "stop-posts"), target + "\n");
                    File.WriteAllText(Path.Combine(control, "stop-request.json"), new string(body));
                    var mode = Mode("stop-mode");
                    if (mode == "error") { await Reply(409, new { ok = false, error = new { code = "OUTPUT_PATH_IN_USE" } }); return; }
                    var prior = Mode("state"); if (prior == "") prior = "pending_confirmation";
                    var stoppedState = prior is "pending_confirmation" or "preparing" or "countdown" ? "cancelled" : "stopping";
                    if (target == OtherRecording) File.WriteAllText(Path.Combine(control, "other-state"), stoppedState);
                    else { File.WriteAllText(Path.Combine(control, "state"), stoppedState); File.WriteAllText(Path.Combine(control, "stop-reason"), "user_requested"); }
                    if (mode == "hold") while (!File.Exists(Path.Combine(control, "stop-release"))) await Task.Delay(30, lifetime.Token);
                    if (mode == "drop") return;
                    if (mode == "redirect") { await Reply(302, new { }, "Location: http://127.0.0.1:1/steal\r\n"); return; }
                    if (mode == "huge") { await Raw(new string('x', 70000), 70000); return; }
                    if (mode == "truncated") { await Raw("{", 100); return; }
                    if (mode == "timeout") { await Task.Delay(Timeout.Infinite, lifetime.Token); return; }
                    // Stop has its own native projection, never GetStatus's bytes_written shape.
                    await Reply(200, new { ok = true, data = new { recording_id = mode == "wrong-id" ? OtherRecording : target,
                        status = stoppedState, mode = "video", stop_reason = "user_requested", output = stoppedState == "stopping" ? null : Output() } }); return;
                }
                if (first[0] == "POST" && route == "/api/v1/recordings/quick")
                {
                    if (length is < 1 or > 4096) return;
                    var body = new char[length];
                    if (await reader.ReadBlockAsync(body.AsMemory(), lifetime.Token) != length) throw new IOException();
                    var json = new string(body); File.AppendAllText(Path.Combine(control, "posts"), "post\n"); File.WriteAllText(Path.Combine(control, "request.json"), json);
                    using var request = JsonDocument.Parse(json); duration = request.RootElement.GetProperty("duration_seconds").GetInt32();
                    var mode = Mode("post-mode");
                    if (mode == "error") { await Reply(409, new { ok = false, error = new { code = "OUTPUT_PATH_IN_USE", message = "SECRET-fixture-key" } }); return; }
                    if (mode is "selection_cancelled" or "selection_timeout")
                    { await Reply(200, new { ok = true, data = new { status = mode, quick = new { recording_created = false } } }); return; }
                    while (!File.Exists(Path.Combine(control, "selected"))) await Task.Delay(30, lifetime.Token);
                    File.WriteAllText(Path.Combine(control, "native-created"), Recording);
                    if (mode == "drop") return;
                    if (mode == "redirect") { await Reply(302, new { }, "Location: http://127.0.0.1:1/steal\r\n"); return; }
                    if (mode == "huge") { await Raw(new string('x', 70000), 70000); return; }
                    if (mode == "truncated") { await Raw("{", 100); return; }
                    if (mode == "timeout") { await Task.Delay(Timeout.Infinite, lifetime.Token); return; }
                    await Reply(200, new { ok = true, data = new { status = "requires_user_confirmation", recording_id = Recording, confirmation_id = Confirmation,
                        quick = new { recording_created = true }, secret = "SECRET-fixture-key" } }); return;
                }
                var state = Mode("state"); if (state == "") state = "pending_confirmation";
                if (!File.Exists(Path.Combine(control, "approved")) && state is "preparing" or "countdown" or "recording" or "completed") state = "pending_confirmation";
                if (state == "recording") File.WriteAllText(Path.Combine(control, "capture-count"), "synthetic");
                if (route == "/api/v1/recordings/" + Recording)
                {
                    if (Mode("query-mode") == "timeout") await Task.Delay(Timeout.Infinite, lifetime.Token);
                    bool pause; lock (tasks)
                    {
                        pause = Mode("query-pause") == "once";
                        if (pause) { File.Delete(Path.Combine(control, "query-pause")); File.WriteAllText(Path.Combine(control, "query-observed"), state); }
                    }
                    if (pause) while (!File.Exists(Path.Combine(control, "query-release"))) await Task.Delay(30, lifetime.Token);
                    await Reply(200, new { ok = true, data = new { recording_id = Mode("query-mode") == "wrong-id" ? "rec_ffffffffffff" : Recording,
                        status = state, mode = "video", stop_reason = Mode("stop-reason") is { Length: > 0 } reason ? reason : state switch { "rejected" => "user_rejected", "expired" => "confirmation_expired", _ => "duration_reached" }, output = StatusOutput() } }); return;
                }
                if (route == "/api/v1/recordings/" + OtherRecording)
                { await Reply(200, new { ok = true, data = new { recording_id = OtherRecording, status = Mode("other-state"), mode = "video" } }); return; }
                if (route == "/api/v1/recordings/" + Recording + "/output")
                {
                    await Reply(200, new { ok = true, data = new { recording_id = Recording, status = state, mode = "video", output = Output(), secret = "SECRET-fixture-key" } }); return;
                }
                await Reply(404, new { ok = false, error = new { code = "RECORDING_NOT_FOUND" } });
                object StatusOutput() => new { path = Path.Combine(data, "synthetic.mp4"), bytes_written = 4096,
                    duration_seconds = ActualDuration(), container = "mp4", codec = "h264", width = 640, height = 480 };
                object Output() => new { path = Path.Combine(data, "synthetic.mp4"), size_bytes = 4096, exists = Mode("output-mode") != "missing",
                    container = "mp4", codec = "h264", width = 640, height = 480, fps = 30,
                    duration_seconds = Mode("output-mode") == "bad-duration" ? 0 : ActualDuration(),
                    has_audio_stream = Mode("output-mode") == "audio", probe_streams = new[] { new { codec_type = "video", codec_name = "h264" } } };
                double ActualDuration() => double.TryParse(Mode("output-duration"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : duration;
                async Task Reply(int code, object value, string headers = "")
                {
                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nContent-Type: application/json\r\n{headers}\r\n"), lifetime.Token);
                    await stream.WriteAsync(bytes, lifetime.Token);
                }
                async Task Raw(string value, int expected)
                { await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {expected}\r\nConnection: close\r\n\r\n"), lifetime.Token); await stream.WriteAsync(Encoding.UTF8.GetBytes(value), lifetime.Token); }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
        }
    }
}
