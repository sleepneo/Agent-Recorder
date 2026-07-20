using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

    [Fact]
    public async Task RunAsync_Utf8StderrWithTrailingLines_DrainsCompletely()
    {
        Assert.True(File.Exists(PowerShellPath), $"PowerShell not found at {PowerShellPath}");

        var scriptPath = Path.Combine(_tmpDir, "stderr-drain.ps1");
        WriteUtf8StderrScript(scriptPath);

        var args = new[]
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", scriptPath
        };

        var runner = new ExternalProcessRunner();
        var result = await runner.RunAsync(PowerShellPath, args, TimeSpan.FromSeconds(5), captureStderr: true, stderrEncoding: Encoding.UTF8);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("耳机 (AirPods Pro)", result.Stderr);
        Assert.Contains("EOF_SENTINEL_耳机", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_EofSentinelPreserved_TwentyConsecutiveRuns()
    {
        Assert.True(File.Exists(PowerShellPath), $"PowerShell not found at {PowerShellPath}");

        var scriptPath = Path.Combine(_tmpDir, "stderr-drain.ps1");
        WriteUtf8StderrScript(scriptPath);

        var args = new[]
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", scriptPath
        };

        var runner = new ExternalProcessRunner();
        for (int i = 0; i < 20; i++)
        {
            var result = await runner.RunAsync(PowerShellPath, args, TimeSpan.FromSeconds(5), captureStderr: true, stderrEncoding: Encoding.UTF8);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("EOF_SENTINEL_耳机", result.Stderr);
        }
    }

    private static void WriteUtf8StderrScript(string path)
    {
        // Write a large (but under 4000 char) UTF-8 stderr payload and place the
        // sentinel immediately before the process exits. The script is saved with
        // a UTF-8 BOM so Windows PowerShell reads the Chinese string literals
        // correctly, and the raw stderr stream is written with explicit UTF-8
        // bytes so the runner decodes them as UTF-8.
        File.WriteAllText(path,
            "$utf8 = [System.Text.Encoding]::UTF8\n" +
            "$err = [System.Console]::OpenStandardError()\n" +
            "$writer = New-Object System.IO.StreamWriter($err, $utf8, 1024, $true)\n" +
            "$chunk = '耳机 (AirPods Pro) ' * 150\n" +
            "$writer.Write($chunk)\n" +
            "$writer.Write('EOF_SENTINEL_耳机')\n" +
            "$writer.Flush()\n",
            Encoding.UTF8);
    }
}
