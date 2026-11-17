using System.Diagnostics;
using System.Text.Json;
using AgentRecorder.LifecycleTests;

if (args.Contains("--lifecycle-job-child")) return await LifecycleLaunch.RunJobChild(args);

// Synthetic executable, never connected to Recorder, a desktop, HTTP or capture.
var root = args[Array.IndexOf(args, "--package-root") + 1];
var data = args[Array.IndexOf(args, "--data-dir") + 1];
var app = args[Array.IndexOf(args, "--app") + 1];
Directory.CreateDirectory(Path.Combine(data, "runtime"));
File.AppendAllText(Path.Combine(data, "runtime", "cli-arguments.jsonl"), JsonSerializer.Serialize(args) + "\n");
File.WriteAllText(Path.Combine(data, "runtime", "cli-pid"), Environment.ProcessId.ToString());
var modePath = Path.Combine(root, "fixture-mode.txt");
var mode = File.Exists(modePath) ? File.ReadAllText(modePath) : "ready";
if (mode == "lifecycle-new") await LifecycleLaunch.StartShellHost(app, data);
if (mode == "lifecycle-borrow")
    LifecycleObservation.WriteSelf(File.ReadAllText(Path.Combine(root, "lifecycle-control-path.txt")), "cli-before-borrowed-observation");
if (mode.StartsWith("error:"))
{
    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, code = mode[6..], message = "SECRET-fixture-key" }));
    return 2;
}
if (mode is "borrow" or "timeout" or "cancel" or "disconnect" or "flood")
{
    var host = Process.Start(new ProcessStartInfo(app)
    {
        UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
        ArgumentList = { "--signal", Path.Combine(data, "runtime", "host-exit"), "--receipt", Path.Combine(data, "runtime", "host.json") }
    })!;
    host.Dispose(); // Borrowed fake host has its own normal-exit signal.
}
if (mode == "borrow") await Task.Delay(200);
if (mode == "flood")
{
    // Both pipes exceed capacity; parent must drain concurrently, retain no logs, and not kill us.
    var flood = string.Concat(Enumerable.Repeat("SECRET-fixture-key", 10000));
    await Task.WhenAll(Console.Out.WriteAsync(flood), Console.Error.WriteAsync(flood));
    await Console.Out.FlushAsync(); await Console.Error.FlushAsync();
    await Task.Delay(600);
}
if (mode == "timeout") await Task.Delay(17000);
if (mode is "cancel" or "disconnect") await Task.Delay(1500);
Directory.CreateDirectory(Path.Combine(data, "state"));
Directory.CreateDirectory(Path.Combine(data, "config"));
var databasePath = Path.Combine(data, "state", "agent-recorder.db");
if (!File.Exists(databasePath)) File.WriteAllBytes(databasePath, "SQLite format 3\0synthetic only"u8.ToArray());
File.WriteAllText(Path.Combine(data, "config", "api-key.txt"), "SECRET-fixture-key");
File.WriteAllText(Path.Combine(data, "runtime", "cli-retired"), "normal");
var readyFile = Path.Combine(data, "runtime", "ready.json");
if (File.Exists(readyFile))
{
    using var ready = JsonDocument.Parse(File.ReadAllText(readyFile));
    Console.WriteLine(JsonSerializer.Serialize(new { ok = true, status = "ready", mode = "tray", desktop_status = "interactive",
        started = mode == "lifecycle-new", api_version = "v1", pid = ready.RootElement.GetProperty("pid").GetInt32(), port = ready.RootElement.GetProperty("port").GetInt32(),
        data_dir = data, api_key_file = Path.Combine(data, "config", "api-key.txt"), ready_file = readyFile }));
    return 0;
}
try { Console.WriteLine(JsonSerializer.Serialize(new
{
    ok = true, status = "ready", mode = "tray", desktop_status = "interactive",
    started = mode != "reuse", api_version = mode == "bad-api" ? "0.1.25" : "v1",
    pid = Environment.ProcessId, port = 24567,
    data_dir = mode == "bad-data" ? root : data,
    api_key_file = Path.Combine(data, "config", "api-key.txt"),
    ready_file = Path.Combine(data, "runtime", "ready.json"), unwanted_secret = "SECRET-fixture-key"
})); }
catch (IOException) { /* Observation pipe can close. This is not a host stop signal. */ }
return 0;
