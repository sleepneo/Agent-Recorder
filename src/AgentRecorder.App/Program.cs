using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Logging;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            LogStartupError(ex);
            throw;
        }
    }

    private static void Run()
    {
        // Start timing as early as possible.
        var readiness = new RuntimeReadiness("tray", ApiServer.Port);

        // Single-instance guard BEFORE ready-file cleanup.
        // If another instance already holds the mutex, we must NOT delete
        // its ready.json or bind the API port.
        var instanceGuard = SingleInstanceGuard.TryAcquire();

        ApplicationConfiguration.Initialize();
        var audit = new AuditLogger();

        if (!instanceGuard.IsAcquired)
        {
            // Another instance is already running. Log diagnostics and exit.
            var existingSnapshot = SingleInstanceGuard.ReadExistingReadyFile(readiness.DataDir);
            audit.Log("service.instance_already_running", new
            {
                mode = "tray",
                pid = Environment.ProcessId,
                mutex_name = SingleInstanceGuard.MutexName,
                existing_pid = existingSnapshot?.Pid ?? 0,
                ready_file = existingSnapshot?.ReadyFile ?? readiness.ReadyFilePath,
                note = "second instance exiting without binding port or deleting ready file"
            });
            instanceGuard.Dispose();
            return;
        }

        audit.Log("service.instance_acquired", new
        {
            mode = "tray",
            pid = Environment.ProcessId,
            mutex_name = SingleInstanceGuard.MutexName
        });

        // Now safe to clean up stale ready.json (we own the instance).
        readiness.CleanupOldReadyFile();

        Application.ThreadException += (_, e) =>
        {
            try
            {
                audit.Log("service.ui_thread_exception", new
                {
                    mode = "tray",
                    error = e.Exception.Message,
                    type = e.Exception.GetType().FullName,
                    stack = e.Exception.ToString()
                });
            }
            catch { }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                audit.Log("service.unhandled_exception", new
                {
                    mode = "tray",
                    is_terminating = e.IsTerminating,
                    error = ex?.Message ?? "unknown",
                    type = ex?.GetType().FullName ?? "unknown",
                    stack = ex?.ToString() ?? ""
                });
            }
            catch { }
        };

        var engine = new RecordingEngine(audit);
        var tray = new TrayContext(engine, audit);
        engine.SetTray(tray);

        var appExePath = Application.ExecutablePath;
        var autoStart = new WindowsAutoStartManager(appExePath);
        var ffmpegPrewarmer = new FfmpegPrewarmer();

        var server = new ApiServer(engine, audit, tray, readiness, autoStart, ffmpegPrewarmer);

        audit.Log("service.starting", new { mode = "tray", port = ApiServer.Port, pid = Environment.ProcessId });
        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            audit.Log("service.start_failed", new { mode = "tray", error = ex.Message, type = ex.GetType().FullName });
            throw;
        }
        audit.Log("service.started", new { mode = "tray", port = ApiServer.Port, pid = Environment.ProcessId });

        // Mark readiness: write ready.json, set named event.
        var snapshot = readiness.MarkReady();
        audit.Log("service.api_ready", new
        {
            mode = snapshot.Mode,
            port = snapshot.Port,
            pid = snapshot.Pid,
            startup_elapsed_ms = snapshot.StartupElapsedMs,
            ready_file = snapshot.ReadyFile,
            named_event = snapshot.NamedEvent
        });
        audit.Log("service.ready_file_written", new { path = snapshot.ReadyFile, pid = snapshot.Pid });

        // Kick off FFmpeg prewarm in background - does not block readiness.
        ffmpegPrewarmer.Start(audit);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                engine.StopAllSync("process_exit");
                audit.Log("service.stopped", new { mode = "tray", reason = "process_exit", pid = Environment.ProcessId });
                server.Stop();
                CleanupReadiness(readiness, audit);
                audit.Log("service.instance_released", new { mode = "tray", pid = Environment.ProcessId, mutex_name = SingleInstanceGuard.MutexName });
                instanceGuard.Dispose();
            }
            catch { }
        };

        Application.ApplicationExit += (_, _) =>
        {
            engine.StopAllSync("application_exit");
            audit.Log("service.stopped", new { mode = "tray", reason = "application_exit", pid = Environment.ProcessId });
            server.Stop();
            CleanupReadiness(readiness, audit);
            audit.Log("service.instance_released", new { mode = "tray", pid = Environment.ProcessId, mutex_name = SingleInstanceGuard.MutexName });
            instanceGuard.Dispose();
        };
        Application.Run(tray);
    }

    private static void CleanupReadiness(RuntimeReadiness readiness, AuditLogger audit)
    {
        try
        {
            var deleteResult = readiness.TryDeleteReadyFile();
            if (deleteResult.Success)
            {
                audit.Log("service.ready_file_deleted",
                    new { pid = Environment.ProcessId, path = deleteResult.Path, was_present = deleteResult.WasPresent });
            }
            else
            {
                audit.Log("service.ready_file_delete_failed",
                    new { pid = Environment.ProcessId, path = deleteResult.Path, error = deleteResult.Error, type = deleteResult.ErrorType });
            }
            readiness.Dispose();
        }
        catch (Exception ex)
        {
            try
            {
                audit.Log("service.ready_file_delete_failed",
                    new { pid = Environment.ProcessId, error = ex.Message, type = ex.GetType().FullName });
            }
            catch { }
        }
    }

    private static void LogStartupError(Exception ex)
    {
        try
        {
            var dataDir = DataDirResolver.Resolve();
            var logDir = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "startup-errors.jsonl");
            var entry = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                type = ex.GetType().FullName,
                message = ex.Message,
                stack = ex.ToString()
            };
            File.AppendAllText(logPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch
        {
            // Best-effort logging; do not mask the original exception.
        }
    }
}
