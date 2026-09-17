using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Logging;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;

namespace AgentRecorder.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
#if DEBUG
            if (ConfirmationThemePreviewHost.TryRun(args))
                return;
            if (RegionSelectionStylePreviewHost.TryRun(args))
                return;
            if (RecordingStatusStylePreviewHost.TryRun(args))
                return;
#endif
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
        // Normalize the hidden encoder policy once at process startup. Empty
        // means software; invalid values fail before WGC warmup or recording.
        Environment.SetEnvironmentVariable(
            WgcEncoderModePolicy.EnvironmentVariable,
            WgcEncoderModePolicy.ToArgumentValue(WgcEncoderModePolicy.NormalizeEnvironment()),
            EnvironmentVariableTarget.Process);

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

        // Operational SQLite belongs only to the process which owns the
        // single-instance guard. The second-instance path above must not even
        // construct the store, because construction can resolve/create paths.
        var operationalStore = InitializeOperationalStoreIfOwner(instanceGuard.IsAcquired, audit.Log)!;

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

        var dataDir = DataDirResolver.Resolve();

        // Cleanup failed-recording diagnostics older than 24 hours. Failure here
        // must not block startup, and the scope is limited to <data-dir>/failed/.
        try
        {
            new TempRetentionPolicy(dataDir).Cleanup();
        }
        catch
        {
            // Best-effort cleanup; do not prevent application startup.
        }

        var perfTracer = new RecordingPerformanceTracer(dataDir);

        // Production microphone device provider: owned by the engine and shared
        // with /audio/devices, /permissions, and /capabilities via the engine
        // reference. Keeps a short TTL cache so multiple API calls within a few
        // seconds do not repeatedly spawn FFmpeg device-enumeration processes.
        var micProvider = new CachingMicrophoneDeviceProvider(new FfmpegDshowMicrophoneProvider());
        var micStatusProvider = new CoreAudioCaptureStatusProvider();
        var systemAudioEndpointProvider = new CoreAudioSystemAudioEndpointProvider();

        var bundleGenerator = new FfmpegRecordingBundleGenerator();
        var standingStartSafetyInterlock = new StandingLeaseStartSafetyInterlock();
        StandingLeaseSafetyControlService? unattendedSafetyService = null;
        var engine = new RecordingEngine(
            audit,
            perfTracer,
            bundleGenerator,
            micProvider,
            micStatusProvider,
            displayTopologyProvider: null,
            systemAudioEndpointProvider: systemAudioEndpointProvider,
            standingStartSafetyInterlock: standingStartSafetyInterlock,
            standingStartSafetyValidator: (ticket, nowUtc) => unattendedSafetyService is null
                ? "standing_safety_service_unavailable"
                : unattendedSafetyService.ValidateStandingStart(ticket, nowUtc));
        unattendedSafetyService = new StandingLeaseSafetyControlService(
            operationalStore,
            utcNowForTest: null,
            activeRunStopper: new RecordingEngineStandingLeaseActiveRunStopper(engine),
            startSafetyInterlock: standingStartSafetyInterlock);
        var tray = new TrayContext(
            engine,
            audit,
            hotkeyFactory: null,
            tracer: perfTracer,
            unattendedSafetyService: unattendedSafetyService);
        engine.SetTray(tray);
        StandingLeaseNaturalWakeRuntime? standingNaturalWakeRuntime = null;
        var standingPlanSetupCoordinator = new StandingPlanSetupCoordinator(
            operationalStore,
            audit,
            tray,
            unattendedSafetyService,
            () => standingNaturalWakeRuntime?.ExecutionSupported == true,
            engine.HasRecording);
        standingNaturalWakeRuntime = new StandingLeaseNaturalWakeRuntime(
            operationalStore,
            engine,
            tray,
            audit);
        if (!standingNaturalWakeRuntime.Start())
        {
            audit.Log("standing_lease.runtime_blocked", new
            {
                reason_code = "startup_recovery_or_scheduler_failed",
                execution_supported = false,
            });
        }

        var appExePath = Application.ExecutablePath;
        var autoStart = new WindowsAutoStartManager(appExePath);
        var ffmpegPrewarmer = new FfmpegPrewarmer();

        var ensureContextStore = new EnsureContextStore(dataDir);
        var perfSummaryProvider = new RollingJsonlPerformanceSummaryProvider(dataDir);
        var server = new ApiServer(
            engine,
            audit,
            tray,
            readiness,
            autoStart,
            ffmpegPrewarmer,
            perfTracer,
            ensureContextStore,
            perfSummaryProvider,
            standingPlanSetupCoordinator);

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

        var wgcWarmupCts = new CancellationTokenSource();
        _ = WgcContinuousWarmup.StartIfEnabled(
            CaptureBackendSelector.ProductionDisplayProbe,
            message => Debug.WriteLine(message),
            wgcWarmupCts.Token);
        int wgcWarmupStopped = 0;
        void StopWgcWarmup()
        {
            if (Interlocked.Exchange(ref wgcWarmupStopped, 1) != 0)
                return;
            try { wgcWarmupCts.Cancel(); } catch { }
            wgcWarmupCts.Dispose();
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                StopWgcWarmup();
                standingNaturalWakeRuntime?.Dispose();
                engine.StopAllSync("process_exit");
                audit.Log("service.stopped", new { mode = "tray", reason = "process_exit", pid = Environment.ProcessId });
                server.Stop();
                CleanupReadiness(readiness, audit);
                audit.Log("service.instance_released", new { mode = "tray", pid = Environment.ProcessId, mutex_name = SingleInstanceGuard.MutexName });
                instanceGuard.Dispose();
                perfTracer.Dispose();
                standingPlanSetupCoordinator.Dispose();
            }
            catch { }
        };

        Application.ApplicationExit += (_, _) =>
        {
            StopWgcWarmup();
            standingNaturalWakeRuntime?.Dispose();
            engine.StopAllSync("application_exit");
            audit.Log("service.stopped", new { mode = "tray", reason = "application_exit", pid = Environment.ProcessId });
            server.Stop();
            CleanupReadiness(readiness, audit);
            audit.Log("service.instance_released", new { mode = "tray", pid = Environment.ProcessId, mutex_name = SingleInstanceGuard.MutexName });
            instanceGuard.Dispose();
            perfTracer.Dispose();
            standingPlanSetupCoordinator.Dispose();
        };
        Application.Run(tray);
    }

    internal static SqliteOperationalStore? InitializeOperationalStoreIfOwner(
        bool instanceAcquired,
        Action<string, object> audit,
        Func<SqliteOperationalStore>? storeFactoryForTest = null)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (!instanceAcquired)
            return null;

        var operationalStore = storeFactoryForTest?.Invoke() ?? new SqliteOperationalStore();
        try
        {
            operationalStore.Initialize();
            audit("service.operational_store_ready", new
            {
                database_path = operationalStore.DatabasePath,
                schema_version = SqliteOperationalStore.CurrentSchemaVersion,
            });
            return operationalStore;
        }
        catch (Exception ex)
        {
            audit("service.operational_store_init_failed", new
            {
                database_path = operationalStore.DatabasePath,
                error = ex.Message,
                type = ex.GetType().FullName,
            });
            throw;
        }
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
