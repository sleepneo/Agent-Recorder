using System.Diagnostics;
using AgentRecorder.LifecycleTests;

internal static class LifecycleLaunch
{
    public static async Task StartShellHost(string app, string data)
    {
        var control = Path.Combine(data, "lifecycle-control"); Directory.CreateDirectory(control);
        LifecycleObservation.WriteSelf(control, "cli-before-shell-launch");
        var start = HostStart(app, data, control, shell: true);
        var previous = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR");
        Process? host;
        try { Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", data); host = Process.Start(start); }
        finally { Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", previous); }
        using (host)
        {
            if (host == null) throw new IOException("synthetic_shell_launch_failed");
            File.WriteAllText(Path.Combine(control, "child-created.json"), System.Text.Json.JsonSerializer.Serialize(
                LifecycleObservation.Capture(host, "shell_child_created_before_readiness", Environment.ProcessId)));
            await WaitReady(control);
            File.WriteAllText(Path.Combine(control, "launch.json"), System.Text.Json.JsonSerializer.Serialize(new
            { creator_pid = Environment.ProcessId, child = LifecycleObservation.Capture(host, "after-shell-launch", Environment.ProcessId),
                use_shell_execute = true, window_style = "Hidden", redirect_standard_input = false,
                redirect_standard_output = false, redirect_standard_error = false,
                temporary_data_environment_restored = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR") == previous,
                os_parent_relation = "unknown_shell_may_delegate" }));
        }
        LifecycleObservation.WriteSelf(control, "cli-before-normal-exit");
    }

    public static async Task<int> RunJobChild(string[] args)
    {
        string Arg(string name) => args[Array.IndexOf(args, name) + 1];
        var app = Arg("--app"); var data = Arg("--data-dir"); var control = Arg("--control");
        if (File.Exists(Path.Combine(control, "abort"))) return 0;
        LifecycleObservation.WriteSelf(control, "model-cli-before-launch");
        // Deliberately CreateProcess-style for deterministic Job inheritance.
        // This differs from product ShellExecute and is labelled a model only.
        using var host = Process.Start(HostStart(app, data, control, shell: false))!;
        await WaitReady(control);
        File.WriteAllText(Path.Combine(control, "model-launch.json"), System.Text.Json.JsonSerializer.Serialize(new
        { creator_pid = Environment.ProcessId, child = LifecycleObservation.Capture(host, "model-child-created", Environment.ProcessId),
            use_shell_execute = false, redirect_standard_input = false, redirect_standard_output = false, redirect_standard_error = false }));
        LifecycleObservation.WriteSelf(control, "model-cli-before-normal-exit");
        return 0;
    }
    private static ProcessStartInfo HostStart(string app, string data, string control, bool shell)
    {
        var start = new ProcessStartInfo(app) { UseShellExecute = shell, WorkingDirectory = Path.GetDirectoryName(app)!,
            WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = !shell };
        foreach (var arg in new[] { "--api-fixture", "--data-dir", data, "--control", control, "--signal", Path.Combine(control, "exit"),
            "--lifecycle-observe", "--lifecycle-launcher", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
        return start;
    }
    private static async Task WaitReady(string control)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(control, "ready")) && timer.ElapsedMilliseconds < 8000) await Task.Delay(30);
        if (!File.Exists(Path.Combine(control, "ready"))) throw new IOException("synthetic_host_ready_timeout");
    }
}
