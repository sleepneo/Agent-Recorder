using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using AgentRecorder.Api;
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
        readiness.CleanupOldReadyFile();

        ApplicationConfiguration.Initialize();
        var audit = new AuditLogger();

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
        var server = new ApiServer(engine, audit, tray, readiness);

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

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                engine.StopAllSync("process_exit");
                audit.Log("service.stopped", new { mode = "tray", reason = "process_exit", pid = Environment.ProcessId });
                server.Stop();
                CleanupReadiness(readiness, audit);
            }
            catch { }
        };

        Application.ApplicationExit += (_, _) =>
        {
            engine.StopAllSync("application_exit");
            audit.Log("service.stopped", new { mode = "tray", reason = "application_exit", pid = Environment.ProcessId });
            server.Stop();
            CleanupReadiness(readiness, audit);
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
            var dataDir = Environment.GetEnvironmentVariable("AGENT_RECORDER_DATA_DIR")
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".local-data");
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
