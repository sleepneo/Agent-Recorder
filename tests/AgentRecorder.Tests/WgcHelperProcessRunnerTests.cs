using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using AgentRecorder.Capture;

namespace AgentRecorder.Tests;

/// <summary>
/// Real-process tests for <see cref="WgcHelperProcessRunner"/>.
/// These tests do not call WGC or capture the screen; they exercise the
/// generic process-runner timeout / kill-tree-and-wait contract.
/// </summary>
public sealed class WgcHelperProcessRunnerTests : IDisposable
{
    private readonly List<Process> _cleanup = new();
    private readonly ITestOutputHelper _output;

    public WgcHelperProcessRunnerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        foreach (var proc in _cleanup)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { /* best effort */ }
            finally
            {
                proc.Dispose();
            }
        }
    }

    [Fact]
    public void Run_ProcessExitsNormally_ReturnsExitCodeAndOutput()
    {
        var runner = new WgcHelperProcessRunner();

        var sw = Stopwatch.StartNew();
        var result = runner.Run(
            "cmd.exe",
            new[] { "/c", "echo hello-world & exit 42" },
            timeoutMs: 5000);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Runner returned too slowly: {sw.Elapsed}");
        Assert.Equal(42, result.ExitCode);
        Assert.Contains("hello-world", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_Timeout_KillsProcessTreeAndReportsFailure()
    {
        var runner = new WgcHelperProcessRunner();

        // Use a repo-style recursive PowerShell fixture generated at runtime.
        // The fixture writes parent, child and grandchild PIDs to a single file,
        // then creates a ready signal before sleeping. This avoids the variable
        // scoping problems of inline -Command scripts.
        var tempDir = Path.Combine(Path.GetTempPath(), $"wgc-tree-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var pidFile = Path.Combine(tempDir, "pids.txt");
        var readyFile = Path.Combine(tempDir, "ready.signal");
        var scriptPath = Path.Combine(tempDir, "hang.ps1");

        try
        {
            var script = $@"
param(
    [string]$PidFile,
    [string]$ReadyFile,
    [int]$Depth = 0
)
$ErrorActionPreference = 'Stop'
Add-Content -Path $PidFile -Value $PID -Encoding ASCII
if ($Depth -lt 2) {{
    Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,$PidFile,$ReadyFile,($Depth+1) -NoNewWindow -PassThru | Out-Null
}} else {{
    New-Item -Path $ReadyFile -ItemType File -Force | Out-Null
}}
Start-Sleep -Seconds 30
";
            File.WriteAllText(scriptPath, script);

            var sw = Stopwatch.StartNew();
            var result = runner.Run(
                "powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, pidFile, readyFile, "0" },
                timeoutMs: 3000);
            sw.Stop();

            var report = new StringBuilder();
            report.AppendLine($"Runner elapsed: {sw.Elapsed}");
            report.AppendLine($"Exit code: {result.ExitCode}");
            report.AppendLine($"Stderr tail: {result.StandardError}");

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Runner returned too slowly: {sw.Elapsed}");
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);

            // The fixture must have reached the grandchild before the runner timed out.
            Assert.True(File.Exists(readyFile), $"Ready signal was not written before the runner timed out.{Environment.NewLine}{report}");

            var pids = ReadDistinctPids(pidFile);
            foreach (var pid in pids)
                RegisterForCleanup(pid);

            Assert.Equal(3, pids.Count);
            Assert.True(pids.All(p => p > 0), $"All three PIDs must be positive. Found: {string.Join(", ", pids)}");

            report.AppendLine($"PIDs: parent={pids[0]}, child={pids[1]}, grandchild={pids[2]}");
            _output.WriteLine($"C# runner tree PIDs: parent={pids[0]}, child={pids[1]}, grandchild={pids[2]}");

            // Unconditional evidence: every captured PID must be gone after kill-tree.
            for (int i = 0; i < pids.Count; i++)
            {
                string label = i == 0 ? "parent" : i == 1 ? "child" : "grandchild";
                bool running = IsProcessRunning(pids[i]);
                report.AppendLine($"{label} PID {pids[i]} running={running}");
                _output.WriteLine($"C# runner tree {label} PID {pids[i]} running={running}");
                Assert.False(running, $"{label} process {pids[i]} is still alive.{Environment.NewLine}{report}");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Run_Timeout_CapturesStdoutAndStderr()
    {
        var runner = new WgcHelperProcessRunner();

        const string command =
            "Write-Output 'stdout-marker'; " +
            "[Console]::Error.WriteLine('stderr-marker'); " +
            "Start-Sleep -Seconds 30";

        var result = runner.Run(
            "powershell.exe",
            new[] { "-NoProfile", "-Command", command },
            timeoutMs: 1500);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("stdout-marker", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("stderr-marker", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_Cancellation_KillsProcessAndReturnsFailure()
    {
        var runner = new WgcHelperProcessRunner();
        using var cts = new CancellationTokenSource();

        const string command = "Start-Sleep -Seconds 30";

        // Cancel shortly after the process starts.
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var sw = Stopwatch.StartNew();
        var result = runner.Run(
            "powershell.exe",
            new[] { "-NoProfile", "-Command", command },
            timeoutMs: 10000,
            cancellationToken: cts.Token);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Runner returned too slowly: {sw.Elapsed}");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("cancelled", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_Cancellation_WithNoTimeout_KillsProcessAndReturnsFailure()
    {
        var runner = new WgcHelperProcessRunner();
        using var cts = new CancellationTokenSource();

        // With timeoutMs == 0 the runner has no business timeout, but caller
        // cancellation must still interrupt the wait within a bounded time.
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var sw = Stopwatch.StartNew();
        var result = runner.Run(
            "powershell.exe",
            new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" },
            timeoutMs: 0,
            cancellationToken: cts.Token);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Runner returned too slowly: {sw.Elapsed}");
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("cancelled", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Run_ConfirmedNormalExitWinsOverCancellation()
    {
        var runner = new WgcHelperProcessRunner();
        var tempDir = Path.Combine(Path.GetTempPath(), $"wgc-race-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var goFile = Path.Combine(tempDir, "go.signal");
        var exitedFile = Path.Combine(tempDir, "exited.signal");
        var scriptPath = Path.Combine(tempDir, "wait.ps1");

        try
        {
            var script = $@"
param([string]$GoFile, [string]$ExitedFile)
while (-not (Test-Path $GoFile)) {{ Start-Sleep -Milliseconds 5 }}
New-Item -Path $ExitedFile -ItemType File -Force | Out-Null
exit 0
";
            File.WriteAllText(scriptPath, script);

            using var cts = new CancellationTokenSource();
            var runTask = Task.Run(() => runner.Run(
                "powershell.exe",
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, goFile, exitedFile },
                timeoutMs: 10000,
                cancellationToken: cts.Token));

            // Wait for the PowerShell process to start waiting on the go file.
            await Task.Delay(300);

            // Release the process and wait until it has signaled that it is
            // about to exit.
            File.WriteAllText(goFile, "");
            var waitSw = Stopwatch.StartNew();
            while (!File.Exists(exitedFile) && waitSw.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(10);
            }
            Assert.True(File.Exists(exitedFile), "Fixture did not create exited signal.");

            // Give the process a bounded window to actually exit, then cancel.
            // Cancellation is issued after the helper has already finished, so
            // the runner must report a confirmed normal exit instead of
            // treating the cancellation as a kill signal.
            await Task.Delay(300);
            cts.Cancel();

            var result = await runTask;

            _output.WriteLine($"Race test result: ExitCode={result.ExitCode}, Stderr={result.StandardError}");
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("cancelled", result.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static List<int> ReadDistinctPids(string pidFile)
    {
        var pids = new List<int>();
        if (!File.Exists(pidFile))
            return pids;

        foreach (var line in File.ReadAllLines(pidFile))
        {
            if (int.TryParse(line.Trim(), out var pid) && pid > 0 && !pids.Contains(pid))
                pids.Add(pid);
        }

        return pids;
    }

    private void RegisterForCleanup(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            _cleanup.Add(proc);
        }
        catch { /* already gone */ }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
