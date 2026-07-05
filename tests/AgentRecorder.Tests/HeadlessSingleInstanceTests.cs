using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Api;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Integration test: second headless instance must NOT delete the first
/// instance's ready.json and must exit cleanly without binding the API port.
/// Uses the real AgentRecorder.Headless.exe process.
///
/// If another Agent Recorder instance is already running on the machine,
/// the test uses it as the "first instance" and verifies that a newly
/// started second instance exits without deleting the ready file.
/// If no instance is running, the test starts its own first instance.
/// </summary>
[Collection("HeadlessHostIntegration")]
public class HeadlessSingleInstanceTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _headlessExe;
    private Process? _firstInstance;
    private bool _startedFirstInstance;

    public HeadlessSingleInstanceTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"headless-si-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);

        // Find the headless EXE from the test output (Debug build)
        var baseDir = AppContext.BaseDirectory;
        _headlessExe = Path.Combine(baseDir, "AgentRecorder.Headless.exe");
        if (!File.Exists(_headlessExe))
        {
            // Fallback: look in project output
            var projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\AgentRecorder.Headless\bin\Debug\net8.0-windows10.0.19041.0"));
            _headlessExe = Path.Combine(projectDir, "AgentRecorder.Headless.exe");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_startedFirstInstance && _firstInstance != null && !_firstInstance.HasExited)
            {
                try { _firstInstance.Kill(); } catch { }
                try { _firstInstance.WaitForExit(5000); } catch { }
                _firstInstance.Dispose();
            }
        }
        catch { }

        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); } catch { }
    }

    private bool HeadlessExeExists => File.Exists(_headlessExe);

    [Fact]
    public void SecondHeadlessInstance_ExitsWithoutDeletingReadyFile()
    {
        if (!HeadlessExeExists)
        {
            // Skip if we can't find the headless EXE
            return;
        }

        // Check if an instance already holds the mutex (even if we can't find its ready file)
        bool mutexAlreadyHeld = false;
        try
        {
            using var testMutex = Mutex.OpenExisting(SingleInstanceGuard.MutexName);
            mutexAlreadyHeld = true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            mutexAlreadyHeld = false;
        }
        catch
        {
            mutexAlreadyHeld = false;
        }

        // Also check default data dir for a ready file with live PID
        bool existingInstanceRunning = false;
        ReadySnapshot? existingSnap = null;
        string existingDataDir = RuntimeReadiness.ResolveDataDir();
        string existingReadyPath = Path.Combine(existingDataDir, "runtime", "ready.json");
        if (File.Exists(existingReadyPath))
        {
            existingSnap = ReadReadySnapshot(existingReadyPath);
            if (existingSnap != null && existingSnap.Ready && IsProcessAlive(existingSnap.Pid))
            {
                existingInstanceRunning = true;
            }
        }

        if (mutexAlreadyHeld || existingInstanceRunning)
        {
            // Another instance already holds the mutex - use it as "first instance"
            // Start a second instance and verify it exits cleanly
            var secondStartInfo = new ProcessStartInfo
            {
                FileName = _headlessExe,
                Arguments = $"--data-dir \"{_dataDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _dataDir
            };
            secondStartInfo.Environment["AGENT_RECORDER_DATA_DIR"] = _dataDir;

            using var secondInstance = Process.Start(secondStartInfo);
            Assert.NotNull(secondInstance);

            // Second instance should exit quickly (detected mutex already held)
            bool secondExited = secondInstance.WaitForExit(10000);
            Assert.True(secondExited, "Second instance should exit quickly when mutex is held");
            Assert.Equal(0, secondInstance.ExitCode);

            // If we found the existing instance's ready file, verify it's still intact
            if (existingInstanceRunning && File.Exists(existingReadyPath))
            {
                var afterSnap = ReadReadySnapshot(existingReadyPath);
                Assert.NotNull(afterSnap);
                Assert.True(afterSnap.Ready);
            }

            // Verify audit log of second instance contains the right event
            var auditPath = Path.Combine(_dataDir, "logs", "audit.jsonl");
            Assert.True(File.Exists(auditPath), "Second instance should have written audit log");
            string auditContent = File.ReadAllText(auditPath);
            Assert.Contains("service.instance_already_running", auditContent);
        }
        else
        {
            // Start our own first instance with a unique data dir
            var firstStartInfo = new ProcessStartInfo
            {
                FileName = _headlessExe,
                Arguments = $"--data-dir \"{_dataDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _dataDir
            };
            firstStartInfo.Environment["AGENT_RECORDER_DATA_DIR"] = _dataDir;

            _firstInstance = Process.Start(firstStartInfo);
            Assert.NotNull(_firstInstance);
            Assert.False(_firstInstance.HasExited, "First instance should start");
            _startedFirstInstance = true;

            // Wait for first instance to become ready (up to 15s)
            var readyPath = Path.Combine(_dataDir, "runtime", "ready.json");
            bool firstReady = WaitForFile(readyPath, 15000);
            Assert.True(firstReady, "First instance should write ready.json");

            // Read the first instance's ready file
            var firstSnap = ReadReadySnapshot(readyPath);
            Assert.NotNull(firstSnap);
            Assert.True(firstSnap.Ready);
            int firstPid = firstSnap.Pid;
            Assert.True(firstPid > 0);

            // Start second instance
            var secondStartInfo = new ProcessStartInfo
            {
                FileName = _headlessExe,
                Arguments = $"--data-dir \"{_dataDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _dataDir
            };
            secondStartInfo.Environment["AGENT_RECORDER_DATA_DIR"] = _dataDir;

            using var secondInstance = Process.Start(secondStartInfo);
            Assert.NotNull(secondInstance);

            // Second instance should exit quickly (it detected the mutex and bailed)
            bool secondExited = secondInstance.WaitForExit(10000);
            Assert.True(secondExited, "Second instance should exit quickly");
            Assert.Equal(0, secondInstance.ExitCode);

            // First instance should still be running
            Assert.False(_firstInstance.HasExited, "First instance should still be running");

            // ready.json should still exist (second instance must NOT delete it)
            Assert.True(File.Exists(readyPath), "Second instance must NOT delete first instance's ready.json");

            // The ready.json should still reference the first instance's PID
            var afterSnap = ReadReadySnapshot(readyPath);
            Assert.NotNull(afterSnap);
            Assert.Equal(firstPid, afterSnap.Pid);
            Assert.True(afterSnap.Ready);
        }
    }

    private static bool WaitForFile(string path, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (File.Exists(path))
            {
                try
                {
                    var snap = ReadReadySnapshot(path);
                    if (snap != null && snap.Ready) return true;
                }
                catch { }
            }
            Thread.Sleep(200);
        }
        return false;
    }

    private static ReadySnapshot? ReadReadySnapshot(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ReadySnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
        }
        catch
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
