using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.Headless;

internal static class Program
{
    private static readonly ManualResetEventSlim _exitSignal = new(false);
    private static readonly object _shutdownLock = new();
    private static int _shutdownStarted;
    private static ApiServer? _server;
    private static RecordingEngine? _engine;
    private static AuditLogger? _audit;
    private static string? _dataDir;
    private static string? _shutdownEventName;
    private static RuntimeReadiness? _readiness;
    private static SingleInstanceGuard? _instanceGuard;
    private static FfmpegPrewarmer? _ffmpegPrewarmer;
    private const string DefaultShutdownEventName = "AgentRecorder.Headless.Shutdown";

    // Windows console APIs: optional signal handling and console detach.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler? handler, bool add);

    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;
    private const uint CTRL_CLOSE_EVENT = 2;
    private const uint CTRL_LOGOFF_EVENT = 5;
    private const uint CTRL_SHUTDOWN_EVENT = 6;

    private static int Main(string[] args)
    {
        // Start timing as early as possible.
        _readiness = new RuntimeReadiness("headless", ApiServer.Port);

        // Detach from the launching console as early as possible so that the
        // headless host does not get terminated when the parent PowerShell/script
        // closes its console or job object.
        try { FreeConsole(); } catch { }

        // Optional: capture console control signals if a console still exists.
        try { SetConsoleCtrlHandler(OnConsoleCtrl, true); } catch { }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            LogShutdown("unhandled_exception", ex?.Message ?? "Unknown");
            LogStartupError(ex ?? new Exception("Unhandled non-exception object"));
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            LogShutdown("process_exit");
            Shutdown();
        };

        try
        {
            var opts = ParseArgs(args);
            _shutdownEventName = string.IsNullOrWhiteSpace(opts.ShutdownEventName)
                ? DefaultShutdownEventName
                : opts.ShutdownEventName;

            // Named event allows an external stop script to request graceful shutdown.
            // The event is created in a background thread so it survives the main loop.
            var shutdownThread = new Thread(ShutdownEventListener) { IsBackground = true };
            shutdownThread.Start();

            ApplyEnvironment(opts);

            // Single-instance guard BEFORE ready-file cleanup.
            // If another instance already holds the mutex, we must NOT delete
            // its ready.json or bind the API port.
            _instanceGuard = SingleInstanceGuard.TryAcquire();
            if (!_instanceGuard.IsAcquired)
            {
                // Audit logger needs data dir resolved, so create it now.
                var audit = new AuditLogger();
                _audit = audit;
                var existingSnapshot = SingleInstanceGuard.ReadExistingReadyFile(_readiness!.DataDir);
                audit.Log("service.instance_already_running", new
                {
                    mode = "headless",
                    pid = Environment.ProcessId,
                    mutex_name = SingleInstanceGuard.MutexName,
                    existing_pid = existingSnapshot?.Pid ?? 0,
                    ready_file = existingSnapshot?.ReadyFile ?? _readiness.ReadyFilePath,
                    note = "second instance exiting without binding port or deleting ready file"
                });
                _instanceGuard.Dispose();
                _instanceGuard = null;
                return 0;
            }

            // Now safe to clean up stale ready.json (we own the instance).
            _readiness?.CleanupOldReadyFile();

            Run(opts);
            return 0;
        }
        catch (Exception ex)
        {
            LogStartupError(ex);
            return ex.HResult == 0 ? 1 : ex.HResult;
        }
    }

    private static void ShutdownEventListener()
    {
        try
        {
            var eventName = _shutdownEventName ?? DefaultShutdownEventName;
            using var evt = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            while (!_exitSignal.IsSet)
            {
                if (evt.WaitOne(1000))
                {
                    LogShutdown("named_event");
                    _exitSignal.Set();
                    break;
                }
            }
        }
        catch { }
    }

    private static bool OnConsoleCtrl(uint ctrlType)
    {
        var reason = ctrlType switch
        {
            CTRL_C_EVENT => "ctrl_c",
            CTRL_BREAK_EVENT => "ctrl_break",
            CTRL_CLOSE_EVENT => "console_close",
            CTRL_LOGOFF_EVENT => "logoff",
            CTRL_SHUTDOWN_EVENT => "shutdown",
            _ => $"console_ctrl_{ctrlType}"
        };
        LogShutdown(reason);
        _exitSignal.Set();
        return true;
    }

    private static HeadlessOptions ParseArgs(string[] args)
    {
        var opts = new HeadlessOptions();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--data-dir":
                    opts.DataDir = GetArgValue(args, ref i, "--data-dir");
                    break;
                case "--ffmpeg-dir":
                    opts.FfmpegDir = GetArgValue(args, ref i, "--ffmpeg-dir");
                    break;
                case "--window-backend":
                    opts.WindowBackend = GetArgValue(args, ref i, "--window-backend");
                    break;
                case "--pid-file":
                    opts.PidFile = GetArgValue(args, ref i, "--pid-file");
                    break;
                case "--shutdown-event-name":
                    opts.ShutdownEventName = GetArgValue(args, ref i, "--shutdown-event-name");
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }
        return opts;
    }

    private static string GetArgValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{name} requires a value.");
        var value = args[++index];
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} value cannot be empty.");
        return value;
    }

    private static void PrintHelp()
    {
        SafeConsoleWrite("AgentRecorder.Headless");
        SafeConsoleWrite("Usage: AgentRecorder.Headless [options]");
        SafeConsoleWrite("Options:");
        SafeConsoleWrite("  --data-dir <path>          Data directory (logs, api-key.txt).");
        SafeConsoleWrite("  --ffmpeg-dir <path>        Directory containing ffmpeg.exe.");
        SafeConsoleWrite("  --window-backend <backend> Window capture backend (wgc). Default: FFmpeg gdigrab.");
        SafeConsoleWrite("  --pid-file <path>          Write process ID to this file on startup.");
        SafeConsoleWrite("  --shutdown-event-name <n>  Windows named event for graceful shutdown.");
        SafeConsoleWrite("  --help, -h                 Show this help.");
    }

    private static void ApplyEnvironment(HeadlessOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.DataDir))
        {
            var absolute = Path.GetFullPath(opts.DataDir);
            Directory.CreateDirectory(absolute);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_DATA_DIR", absolute, EnvironmentVariableTarget.Process);
        }

        if (!string.IsNullOrWhiteSpace(opts.FfmpegDir))
        {
            var absolute = Path.GetFullPath(opts.FfmpegDir);
            Environment.SetEnvironmentVariable("AGENT_RECORDER_FFMPEG_DIR", absolute, EnvironmentVariableTarget.Process);
        }

        if (!string.IsNullOrWhiteSpace(opts.WindowBackend))
        {
            Environment.SetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND", opts.WindowBackend, EnvironmentVariableTarget.Process);
        }
    }

    private static void Run(HeadlessOptions opts)
    {
        _dataDir = DataDirResolver.Resolve();

        WritePidFile(opts.PidFile);

        var audit = new AuditLogger();
        _audit = audit;

        audit.Log("service.instance_acquired", new
        {
            mode = "headless",
            pid = Environment.ProcessId,
            mutex_name = SingleInstanceGuard.MutexName
        });

        var engine = new RecordingEngine(audit);
        var tray = new HeadlessTrayContext(audit);
        engine.SetTray(tray);

        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AgentRecorder.Headless.exe");
        var autoStart = new WindowsAutoStartManager(exePath);
        var ffmpegPrewarmer = new FfmpegPrewarmer();
        _ffmpegPrewarmer = ffmpegPrewarmer;

        var server = new ApiServer(engine, audit, tray, _readiness, autoStart, ffmpegPrewarmer);

        _engine = engine;
        _server = server;

        audit.Log("service.starting", new
        {
            mode = "headless",
            port = ApiServer.Port,
            data_dir = _dataDir,
            ffmpeg_dir = Environment.GetEnvironmentVariable("AGENT_RECORDER_FFMPEG_DIR"),
            window_backend = Environment.GetEnvironmentVariable("AGENT_RECORDER_WINDOW_BACKEND") ?? "ffmpeg_gdigrab",
            shutdown_event = _shutdownEventName,
            pid = Environment.ProcessId
        });

        try
        {
            server.Start();
        }
        catch (Exception ex)
        {
            audit.Log("service.start_failed", new { mode = "headless", error = ex.Message, type = ex.GetType().FullName });
            throw;
        }

        audit.Log("service.started", new { mode = "headless", port = ApiServer.Port, pid = Environment.ProcessId });

        // Mark readiness: write ready.json, set named event.
        var snapshot = _readiness!.MarkReady();
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

        SafeConsoleWrite($"AgentRecorder.Headless listening on http://127.0.0.1:{ApiServer.Port}/ (PID={Environment.ProcessId})");

        _exitSignal.Wait();

        Shutdown();
    }

    private static void WritePidFile(string? pidFile)
    {
        if (string.IsNullOrWhiteSpace(pidFile)) return;
        try
        {
            var absolute = Path.GetFullPath(pidFile);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, Environment.ProcessId.ToString());
        }
        catch
        {
            // Best-effort; do not fail startup because of PID file.
        }
    }

    private static void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
            return; // already shutting down

        lock (_shutdownLock)
        {
            var engine = _engine;
            var server = _server;
            var audit = _audit;
            var readiness = _readiness;
            var instanceGuard = _instanceGuard;
            _engine = null;
            _server = null;
            _audit = null;
            _readiness = null;
            _instanceGuard = null;

            try { engine?.StopAllSync("service_exit"); } catch { }
            try { server?.Stop(); } catch { }
            try { _exitSignal.Set(); } catch { }

            // Cleanup readiness: delete ready.json, release named event.
            if (readiness != null && audit != null)
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

            try
            {
                if (instanceGuard != null && audit != null)
                {
                    audit.Log("service.instance_released",
                        new { mode = "headless", pid = Environment.ProcessId, mutex_name = SingleInstanceGuard.MutexName });
                }
            }
            catch { }

            try { instanceGuard?.Dispose(); } catch { }

            try { audit?.Log("service.stopped", new { mode = "headless", pid = Environment.ProcessId }); } catch { }
        }
    }

    private static void LogShutdown(string reason, string? detail = null)
    {
        try
        {
            var audit = _audit;
            audit?.Log("service.stopping", new { mode = "headless", reason, detail, pid = Environment.ProcessId });
        }
        catch { }
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
                mode = "headless",
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

    private static void SafeConsoleWrite(string text)
    {
        try
        {
            var console = Console.Out;
            console?.WriteLine(text);
        }
        catch { }
    }

    private sealed class HeadlessOptions
    {
        public string? DataDir { get; set; }
        public string? FfmpegDir { get; set; }
        public string? WindowBackend { get; set; }
        public string? PidFile { get; set; }
        public string? ShutdownEventName { get; set; }
    }
}
