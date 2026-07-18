using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public class ExternalProcessRunnerTests : IDisposable
{
    private readonly string _tmpDir;

    public ExternalProcessRunnerTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"epr-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tmpDir))
                Directory.Delete(_tmpDir, recursive: true);
        }
        catch { }
    }

    private static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    private string WriteHelperScript()
    {
        var path = Path.Combine(_tmpDir, "helper.ps1");
        File.WriteAllText(path,
            "param([string]$PidFile)\n" +
            "$PID | Out-File -FilePath $PidFile -Encoding ASCII -Force\n" +
            "while ($true) { Start-Sleep -Milliseconds 200 }\n");
        return path;
    }

    private static async Task<int> WaitForPidAsync(string pidFile, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!File.Exists(pidFile))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }

        var text = await File.ReadAllTextAsync(pidFile);
        if (!int.TryParse(text.Trim(), out var pid))
            throw new InvalidOperationException($"Could not parse PID from '{text}'");
        return pid;
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
    }

    private static void EnsureProcessTreeDead(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.HasExited)
            {
                try { proc.Kill(true); } catch { }
                try { proc.WaitForExit(5000); } catch { }
            }
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_KillsTreeAndWaitsForExit()
    {
        Assert.True(File.Exists(PowerShellPath), $"PowerShell not found at {PowerShellPath}");

        var scriptPath = WriteHelperScript();
        var pidFile = Path.Combine(_tmpDir, "cancel.pid");
        var args = new[]
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", scriptPath,
            "-PidFile", pidFile
        };

        using var cts = new CancellationTokenSource();
        var runner = new ExternalProcessRunner();
        var runTask = runner.RunAsync(PowerShellPath, args, TimeSpan.FromSeconds(30), cancellationToken: cts.Token);

        int pid;
        try
        {
            pid = await WaitForPidAsync(pidFile, TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Best-effort cleanup if handshake failed.
            try { cts.Cancel(); } catch { }
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            throw;
        }

        try
        {
            Assert.True(IsProcessRunning(pid), "Helper PID was not running after handshake");
            cts.Cancel();

            var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
            Assert.IsType<TaskCanceledException>(ex);

            // The runner must have waited for the process to exit before throwing.
            Assert.False(IsProcessRunning(pid), "Helper PID was still running after cancellation");
        }
        finally
        {
            EnsureProcessTreeDead(pid);
        }
    }

    [Fact]
    public async Task RunAsync_Timeout_KillsTreeAndSetsTimedOut()
    {
        Assert.True(File.Exists(PowerShellPath), $"PowerShell not found at {PowerShellPath}");

        var scriptPath = WriteHelperScript();
        var pidFile = Path.Combine(_tmpDir, "timeout.pid");
        var args = new[]
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", scriptPath,
            "-PidFile", pidFile
        };

        var runner = new ExternalProcessRunner();
        var runTask = runner.RunAsync(PowerShellPath, args, TimeSpan.FromMilliseconds(500));

        int pid;
        try
        {
            pid = await WaitForPidAsync(pidFile, TimeSpan.FromSeconds(5));
        }
        catch
        {
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            throw;
        }

        try
        {
            Assert.True(IsProcessRunning(pid), "Helper PID was not running after handshake");

            var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(result.TimedOut, "Expected TimedOut=true");
            // The runner must have waited for the process to exit before returning.
            Assert.False(IsProcessRunning(pid), "Helper PID was still running after timeout");
        }
        finally
        {
            EnsureProcessTreeDead(pid);
        }
    }
}
