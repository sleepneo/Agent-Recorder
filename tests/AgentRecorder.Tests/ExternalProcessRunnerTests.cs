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
        // Publish the PID atomically: write to a temp file in the same directory,
        // then move it to the final path. The reader only observes the final file,
        // so a complete published PID is never confused with a half-written prefix.
        File.WriteAllText(path,
            "param([string]$PidFile)\n" +
            "$tmp = \"$PidFile.tmp\"\n" +
            "$PID | Out-File -FilePath $tmp -Encoding ASCII -Force\n" +
            "Move-Item -Path $tmp -Destination $PidFile -Force\n" +
            "while ($true) { Start-Sleep -Milliseconds 200 }\n");
        return path;
    }

    internal static async Task<int> WaitForPidAsync(string pidFile, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();

        while (true)
        {
            var elapsed = sw.Elapsed;
            if (elapsed >= timeout)
                break;

            if (!File.Exists(pidFile))
            {
                var delay = MinDelay(timeout - elapsed, TimeSpan.FromMilliseconds(50));
                await Task.Delay(delay);
                continue;
            }

            string? text = null;
            try
            {
                text = await File.ReadAllTextAsync(pidFile);
            }
            catch (IOException)
            {
                // The atomic move just completed or the file is briefly locked;
                // retry shortly rather than failing.
                var delay = MinDelay(timeout - elapsed, TimeSpan.FromMilliseconds(50));
                await Task.Delay(delay);
                continue;
            }

            // The publication protocol guarantees that once the final file exists,
            // it contains the complete single-line integer PID. No wall-clock stable
            // window is required.
            if (!string.IsNullOrWhiteSpace(text) && int.TryParse(text.Trim(), out var pid))
                return pid;

            // The final file exists but does not satisfy the protocol (e.g. a
            // leftover invalid file). Wait for a valid publication.
            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(MinDelay(remaining, TimeSpan.FromMilliseconds(50)));
        }

        throw new TimeoutException($"Timed out waiting for a valid PID in '{pidFile}'.");
    }

    private static TimeSpan MinDelay(TimeSpan remaining, TimeSpan max)
    {
        return remaining < max ? remaining : max;
    }

    private static string GetTempPidFile(string pidFile) => pidFile + ".tmp";

    /// <summary>
    /// Publishes a PID deterministically: writes to a temp file in the same
    /// directory, then atomically renames it to the final path. The reader only
    /// observes the final file, so a complete published value is never confused
    /// with a half-written prefix.
    /// </summary>
    private static void PublishPid(string pidFile, int pid)
    {
        var tmp = GetTempPidFile(pidFile);
        File.WriteAllText(tmp, pid.ToString() + "\n", Encoding.ASCII);
        File.Move(tmp, pidFile, overwrite: true);
    }

    /// <summary>
    /// Writes content to the temp file without publishing the final file.
    /// Used to simulate a half-written or invalid PID prefix during tests.
    /// </summary>
    private static void WriteTempPidFile(string pidFile, string content)
    {
        var tmp = GetTempPidFile(pidFile);
        File.WriteAllText(tmp, content + "\n", Encoding.ASCII);
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
    public async Task WaitForPidAsync_MissingFile_ThrowsTimeoutException()
    {
        var pidFile = Path.Combine(_tmpDir, $"missing-{Guid.NewGuid():N}.pid");
        var timeout = TimeSpan.FromMilliseconds(100);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => WaitForPidAsync(pidFile, timeout));
        Assert.Contains(pidFile, ex.Message);
    }

    [Fact]
    public async Task WaitForPidAsync_InvalidPublishedContent_ThrowsTimeoutException()
    {
        var pidFile = Path.Combine(_tmpDir, $"invalid-{Guid.NewGuid():N}.pid");
        PublishPid(pidFile, 123); // first publish a valid PID to create the final file
        File.WriteAllText(pidFile, "not-a-pid"); // then overwrite it with invalid content

        var timeout = TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAsync<TimeoutException>(() => WaitForPidAsync(pidFile, timeout));
    }

    [Fact]
    public async Task WaitForPidAsync_PublishedValidPid_ReturnsPid()
    {
        var pidFile = Path.Combine(_tmpDir, $"valid-{Guid.NewGuid():N}.pid");
        var expectedPid = 12345;

        PublishPid(pidFile, expectedPid);

        var pid = await WaitForPidAsync(pidFile, TimeSpan.FromSeconds(2));
        Assert.Equal(expectedPid, pid);
    }

    [Fact]
    public async Task WaitForPidAsync_TempFileOnly_DoesNotReturnUntilPublished()
    {
        var pidFile = Path.Combine(_tmpDir, $"temp-only-{Guid.NewGuid():N}.pid");

        // Only the temp file exists; the final file has not been published yet.
        WriteTempPidFile(pidFile, "12");

        var timeout = TimeSpan.FromMilliseconds(100);
        await Assert.ThrowsAsync<TimeoutException>(() => WaitForPidAsync(pidFile, timeout));
    }

    [Fact]
    public async Task WaitForPidAsync_HalfWrittenTempThenPublished_ReturnsFullPid()
    {
        var pidFile = Path.Combine(_tmpDir, $"half-then-published-{Guid.NewGuid():N}.pid");
        var expectedPid = 12345;

        // The temp file contains a half-written prefix, but the final file does not exist yet.
        WriteTempPidFile(pidFile, "12");

        // Start the reader before publishing the final value.
        var waitTask = WaitForPidAsync(pidFile, TimeSpan.FromSeconds(2));

        // Deterministically publish the complete PID via atomic rename.
        PublishPid(pidFile, expectedPid);

        var pid = await waitTask;
        Assert.Equal(expectedPid, pid);
    }

    [Fact]
    public async Task WaitForPidAsync_InvalidTempThenValidPublished_ReturnsValidPid()
    {
        var pidFile = Path.Combine(_tmpDir, $"invalid-then-published-{Guid.NewGuid():N}.pid");
        var expectedPid = 55555;

        // Temp file contains non-integer content; final file not yet published.
        WriteTempPidFile(pidFile, "oops");

        var waitTask = WaitForPidAsync(pidFile, TimeSpan.FromSeconds(2));
        PublishPid(pidFile, expectedPid);

        var pid = await waitTask;
        Assert.Equal(expectedPid, pid);
    }

    [Fact]
    public async Task WaitForPidAsync_RePublishedValidPid_ReturnsLatestValidPid()
    {
        var pidFile = Path.Combine(_tmpDir, $"republished-{Guid.NewGuid():N}.pid");

        PublishPid(pidFile, 11111);
        PublishPid(pidFile, 22222);

        var pid = await WaitForPidAsync(pidFile, TimeSpan.FromSeconds(2));
        Assert.Equal(22222, pid);
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
        // Allow enough time for PowerShell cold-start before the runner timeout
        // fires, so the fixture can publish its PID and the test can verify that
        // the timeout path still kills the process tree.
        var runTask = runner.RunAsync(PowerShellPath, args, TimeSpan.FromMilliseconds(2500));

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
