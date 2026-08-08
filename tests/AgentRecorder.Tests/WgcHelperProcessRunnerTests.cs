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
[Collection("NonParallel-RealProcess")]
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
                {
                    proc.Kill(entireProcessTree: true);
                    // Bounded wait: a stuck kill must not block or contaminate
                    // later tests in the shared real-process collection.
                    proc.WaitForExit(5000);
                }
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
    public async Task Run_Timeout_KillsProcessTreeAndReportsFailure()
    {
        var runner = new WgcHelperProcessRunner();

        // Deterministic three-level fixture: the repository's compiled
        // offline helper (WgcRealProcessFixture) with --tree-depth 3. It
        // starts in milliseconds (no nested PowerShell cold-start timing),
        // publishes parent/child and grandchild PIDs via ASCII JSON signal
        // files, and blocks until an external kill -- so the runner's timeout
        // path must be the outcome that wins (Task 196D, Finding B).
        var tempDir = Path.Combine(Path.GetTempPath(), $"wgc-tree-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var helperExe = Path.Combine(tempDir, $"wgc-runner-tree-{Guid.NewGuid():N}.exe");
        var beginSignalPath = Path.Combine(tempDir, "begin.signal");
        var beginToken = Guid.NewGuid().ToString("N");
        var outputPath = Path.Combine(tempDir, "out.bin");
        var readyFile = Path.Combine(tempDir, "wgc-helper-ready.signal");
        var grandchildFile = Path.Combine(tempDir, "wgc-helper-grandchild.signal");

        try
        {
            var helperSourceExe = await WgcRealProcessFixture.GetHelperExePathAsync();
            File.Copy(helperSourceExe, helperExe, overwrite: true);

            // Pre-arm the begin signal so the tree builds immediately and is
            // fully alive long before the runner timeout fires.
            File.WriteAllText(beginSignalPath, beginToken);

            var sw = Stopwatch.StartNew();
            var result = runner.Run(
                helperExe,
                new[] { "--begin-signal", beginSignalPath, "--begin-token", beginToken, "--output", outputPath, "--tree-depth", "3" },
                timeoutMs: 5000);
            sw.Stop();

            var report = new StringBuilder();
            report.AppendLine($"Runner elapsed: {sw.Elapsed}");
            report.AppendLine($"Exit code: {result.ExitCode}");
            report.AppendLine($"TimedOut: {result.TimedOut}");
            report.AppendLine($"Stdout: {result.StandardOutput}");
            report.AppendLine($"Stderr: {result.StandardError}");
            report.AppendLine($"Ready file exists: {File.Exists(readyFile)}");
            report.AppendLine($"Grandchild file exists: {File.Exists(grandchildFile)}");

            // The runner must return within its documented bound: timeout
            // (5s) + kill wait (5s) + reader drain slack.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(13), $"Runner returned too slowly: {sw.Elapsed}{Environment.NewLine}{report}");

            // The timeout path must win explicitly -- an early normal exit
            // must never be silently reinterpreted as a timeout.
            Assert.True(result.TimedOut, $"The configured timeout must win.{Environment.NewLine}{report}");
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("timed out", result.StandardError, StringComparison.OrdinalIgnoreCase);

            // The expected three-level tree must have existed BEFORE the
            // timeout fired; missing evidence fails with full diagnostics.
            Assert.True(File.Exists(readyFile), $"Ready signal was not written before the runner timed out.{Environment.NewLine}{report}");
            Assert.True(File.Exists(grandchildFile), $"Grandchild signal was not written before the runner timed out.{Environment.NewLine}{report}");

            int parentPid = ReadJsonIntProperty(readyFile, "parentPid");
            int childPid = ReadJsonIntProperty(readyFile, "childPid");
            int grandchildPid = ReadJsonIntProperty(grandchildFile, "grandchildPid");

            var pids = new List<int> { parentPid, childPid, grandchildPid };
            foreach (var pid in pids)
                RegisterForCleanup(pid);

            Assert.True(parentPid > 0 && childPid > 0 && grandchildPid > 0,
                $"All three PIDs must be positive. Found: {string.Join(", ", pids)}{Environment.NewLine}{report}");
            Assert.Equal(3, pids.Distinct().Count());

            report.AppendLine($"PIDs: parent={parentPid}, child={childPid}, grandchild={grandchildPid}");
            _output.WriteLine($"Runner tree PIDs: parent={parentPid}, child={childPid}, grandchild={grandchildPid}");

            // Unconditional evidence: root, child, and grandchild are gone
            // after the runner's kill-tree.
            var labels = new[] { "parent", "child", "grandchild" };
            for (int k = 0; k < pids.Count; k++)
            {
                bool running = IsProcessRunning(pids[k]);
                report.AppendLine($"{labels[k]} PID {pids[k]} running={running}");
                Assert.False(running, $"{labels[k]} process {pids[k]} is still alive.{Environment.NewLine}{report}");
            }
        }
        finally
        {
            // Bounded cleanup: kill any fixture-owned PID that survived a
            // failed assertion before deleting the directory. Only PIDs
            // published by this fixture are ever touched (never by image name).
            foreach (var proc in _cleanup)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                    }
                }
                catch { /* best effort */ }
            }
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
    public void Run_ExcessiveOutput_IsBoundedAndMarkedTruncated()
    {
        var runner = new WgcHelperProcessRunner();
        const string command =
            "$s = 'x' * 70000; " +
            "[Console]::Out.Write($s); " +
            "[Console]::Error.Write($s)";

        var result = runner.Run(
            "powershell.exe",
            new[] { "-NoProfile", "-Command", command },
            timeoutMs: 5000);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(64 * 1024, result.StandardOutput.Length);
        Assert.Equal(64 * 1024, result.StandardError.Length);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
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

    /// <summary>
    /// Reads one integer property from a fixture-published ASCII JSON signal
    /// file. Returns -1 when the file or property is missing so callers fail
    /// with their own diagnostics instead of a parse exception.
    /// </summary>
    private static int ReadJsonIntProperty(string path, string property)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(property, out var el) && el.TryGetInt32(out var v) ? v : -1;
        }
        catch { return -1; }
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
