using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Cli;

/// <summary>
/// CLI tool for AI agents to reliably start/reuse Agent Recorder
/// and obtain machine-readable readiness information.
/// </summary>
internal static class Program
{
    private const int DefaultTimeoutSeconds = 30;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        if (command == "ensure-running")
        {
            var opts = ParseOpts(args, 1);
            return RunEnsureRunning(opts);
        }
        if (command == "autostart")
        {
            return RunAutoStart(args);
        }
        return command switch
        {
            "help" or "--help" or "-h" => RunHelp(),
            _ => RunUnknown(command)
        };
    }

    private static CliOptions ParseOpts(string[] args, int startIdx)
    {
        return ParseOptsForTest(args, startIdx);
    }

    internal static CliOptions ParseOptsForTest(string[] args, int startIdx = 1)
    {
        var opts = new CliOptions();
        for (int i = startIdx; i < args.Length; i++)
        {
            var arg = args[i];
            try
            {
                switch (arg)
                {
                    case "--json":
                        opts.Json = true;
                        break;
                    case "--verbose":
                        opts.Verbose = true;
                        break;
                    case "--timeout-ms":
                        var msVal = GetArgValue(args, ref i, "--timeout-ms");
                        if (!int.TryParse(msVal, out var ms))
                            throw new FormatException($"--timeout-ms requires an integer, got '{msVal}'.");
                        opts.TimeoutMs = ms;
                        break;
                    case "--timeout-seconds":
                        var secVal = GetArgValue(args, ref i, "--timeout-seconds");
                        if (!int.TryParse(secVal, out var sec))
                            throw new FormatException($"--timeout-seconds requires an integer, got '{secVal}'.");
                        opts.TimeoutSeconds = sec;
                        break;
                    case "--headless":
                        opts.PreferHeadless = true;
                        break;
                    case "--tray":
                        opts.PreferTray = true;
                        break;
                    case "--data-dir":
                        opts.DataDir = GetArgValue(args, ref i, "--data-dir");
                        break;
                    case "--package-root":
                        opts.PackageRoot = GetArgValue(args, ref i, "--package-root");
                        break;
                    case "--app":
                        opts.AppPath = GetArgValue(args, ref i, "--app");
                        break;
                    case "--help":
                    case "-h":
                        opts.ShowHelp = true;
                        break;
                    default:
                        throw new FormatException($"Unknown option: {arg}");
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
            {
                if (opts.ParseError == null)
                    opts.ParseError = ex.Message;
            }
        }
        return opts;
    }

    private static string GetArgValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new FormatException($"{name} requires a value.");
        return args[++index];
    }

    private static int RunEnsureRunning(CliOptions opts)
    {
        if (opts.ParseError != null)
        {
            var error = new EnsureRunningResult
            {
                Ok = false,
                Status = "error",
                Code = "INVALID_ARGUMENT",
                Message = opts.ParseError,
                SuggestedAction = "Run 'AgentRecorder.Cli.exe ensure-running --help'."
            };
            if (opts.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(error, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }));
            }
            else
            {
                Console.Error.WriteLine($"Error: {opts.ParseError}");
                Console.Error.WriteLine("Run 'AgentRecorder.Cli.exe ensure-running --help'.");
            }
            return 1;
        }

        if (opts.ShowHelp)
        {
            PrintEnsureRunningHelp();
            return 0;
        }

        // Resolve timeout (--timeout-seconds takes precedence over --timeout-ms)
        int timeoutMs = opts.TimeoutSeconds > 0
            ? opts.TimeoutSeconds * 1000
            : (opts.TimeoutMs > 0 ? opts.TimeoutMs : DefaultTimeoutSeconds * 1000);
        opts.TimeoutMs = timeoutMs;

        try
        {
            var result = EnsureRunningCore(opts);
            if (opts.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }));
            }
            else
            {
                if (result.Ok)
                {
                    Console.WriteLine($"Status: {result.Status}");
                    Console.WriteLine($"Mode: {result.Mode}");
                    Console.WriteLine($"PID: {result.Pid}");
                    Console.WriteLine($"Port: {result.Port}");
                    Console.WriteLine($"Ready file: {result.ReadyFile}");
                    Console.WriteLine($"API key file: {result.ApiKeyFile}");
                }
                else
                {
                    Console.Error.WriteLine($"Error: {result.Message}");
                    if (!string.IsNullOrEmpty(result.Code))
                        Console.Error.WriteLine($"Code: {result.Code}");
                    if (!string.IsNullOrEmpty(result.SuggestedAction))
                        Console.Error.WriteLine($"Suggested action: {result.SuggestedAction}");
                }
            }
            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            var error = new EnsureRunningResult
            {
                Ok = false,
                Status = "error",
                Code = "INTERNAL_ERROR",
                Message = ex.Message,
                SuggestedAction = "Check the CLI arguments and try again."
            };
            if (opts.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(error, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                }));
            }
            else
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
            return 1;
        }
    }

    private static int RunAutoStart(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Error: autostart requires a subcommand (status/enable/disable)");
            Console.Error.WriteLine("Run 'AgentRecorder.Cli.exe autostart --help' for usage.");
            return 1;
        }

        var subCmd = args[1].ToLowerInvariant();
        var opts = ParseAutoStartOpts(args, 2);

        if (opts.ParseError != null)
        {
            var error = BuildAutoStartError(opts, "INVALID_ARGUMENT", opts.ParseError,
                "Run 'AgentRecorder.Cli.exe autostart --help'.");
            if (opts.Json)
                WriteAutoStartJson(error);
            else
                Console.Error.WriteLine($"Error: {opts.ParseError}");
            return 1;
        }

        if (opts.ShowHelp || subCmd == "help" || subCmd == "--help" || subCmd == "-h")
        {
            PrintAutoStartHelp();
            return 0;
        }

        try
        {
            var result = RunAutoStartCore(subCmd, opts, new RegistryRunKey());

            if (opts.Json)
            {
                WriteAutoStartJson(result);
            }
            else
            {
                if (result.Ok)
                {
                    Console.WriteLine($"Status: {result.Status}");
                    Console.WriteLine($"Enabled: {result.Enabled}");
                    Console.WriteLine($"Matches current app: {result.MatchesCurrentApp}");
                    Console.WriteLine($"Value name: {result.ValueName}");
                    Console.WriteLine($"Run key: {result.RunKey}");
                    Console.WriteLine($"App path: {result.AppPath}");
                    if (result.ConfiguredCommand != null)
                        Console.WriteLine($"Configured command: {result.ConfiguredCommand}");
                }
                else
                {
                    Console.Error.WriteLine($"Error: {result.Message}");
                    if (!string.IsNullOrEmpty(result.Code))
                        Console.Error.WriteLine($"Code: {result.Code}");
                    if (!string.IsNullOrEmpty(result.SuggestedAction))
                        Console.Error.WriteLine($"Suggested action: {result.SuggestedAction}");
                }
            }

            return result.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            var error = BuildAutoStartError(opts, "INTERNAL_ERROR", ex.Message,
                "Check the CLI arguments and try again.");
            if (opts.Json)
                WriteAutoStartJson(error);
            else
                Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Testable core logic for autostart subcommands. Uses the provided registry
    /// so unit tests can inject a fake without touching real HKCU.
    /// </summary>
    internal static AutoStartResult RunAutoStartCore(string subCommand, AutoStartOptions opts, IRegistryRunKey registry)
    {
        var appPath = ResolveAutoStartAppPath(opts);
        var valueName = opts.ValueName ?? WindowsAutoStartManager.DefaultValueName;
        var manager = new WindowsAutoStartManager(registry, appPath, valueName);

        return subCommand switch
        {
            "status" => ToAutoStartResult(manager.GetStatus(), ok: true),
            "enable" => ToAutoStartResult(manager.Enable()),
            "disable" => ToAutoStartResult(manager.Disable()),
            _ => new AutoStartResult
            {
                Ok = false,
                Status = "error",
                Enabled = false,
                MatchesCurrentApp = false,
                ValueName = valueName,
                RunKey = WindowsAutoStartManager.RunKeyPath,
                AppPath = appPath,
                ConfiguredCommand = null,
                Code = "INVALID_ARGUMENT",
                Message = $"Unknown autostart subcommand: '{subCommand}'",
                SuggestedAction = "Use status, enable, or disable."
            }
        };
    }

    private static AutoStartResult ToAutoStartResult(AutoStartStatus s, bool ok)
    {
        return new AutoStartResult
        {
            Ok = ok && s.Code != "unavailable",
            Status = s.Code ?? "unknown",
            Enabled = s.Enabled,
            MatchesCurrentApp = s.MatchesCurrentApp,
            ValueName = s.ValueName,
            RunKey = s.RunKey,
            AppPath = s.AppPath,
            ConfiguredCommand = s.ConfiguredCommand,
            Code = s.Code,
            Message = s.Message
        };
    }

    private static AutoStartResult ToAutoStartResult(AutoStartOperationResult r)
    {
        return new AutoStartResult
        {
            Ok = r.Ok,
            Status = r.Ok ? (r.Enabled ? "enabled" : "disabled") : "error",
            Enabled = r.Enabled,
            MatchesCurrentApp = r.MatchesCurrentApp,
            ValueName = r.ValueName,
            RunKey = r.RunKey,
            AppPath = r.AppPath,
            ConfiguredCommand = r.ConfiguredCommand,
            Code = r.Code,
            Message = r.Message,
            SuggestedAction = r.SuggestedAction
        };
    }

    private static AutoStartResult BuildAutoStartError(AutoStartOptions opts, string code, string message, string suggestedAction)
    {
        return new AutoStartResult
        {
            Ok = false,
            Status = "error",
            Enabled = false,
            MatchesCurrentApp = false,
            ValueName = opts.ValueName ?? WindowsAutoStartManager.DefaultValueName,
            RunKey = WindowsAutoStartManager.RunKeyPath,
            AppPath = opts.AppPath ?? "",
            ConfiguredCommand = null,
            Code = code,
            Message = message,
            SuggestedAction = suggestedAction
        };
    }

    internal static string ResolveAutoStartAppPath(AutoStartOptions opts)
    {
        return ResolveAutoStartAppPath(opts, AppContext.BaseDirectory);
    }

    /// <summary>
    /// Resolves the App exe path for autostart. Supports:
    /// - Explicit --app (highest priority, used as-is)
    /// - Portable package: &lt;package&gt;/AgentRecorder.App/AgentRecorder.App.exe
    /// - Source tree build: tools/AgentRecorder.Cli/bin/&lt;Config&gt;/&lt;tfm&gt;/ -> src/AgentRecorder.App/bin/&lt;Config&gt;/&lt;tfm&gt;/
    /// </summary>
    internal static string ResolveAutoStartAppPath(AutoStartOptions opts, string baseDir)
    {
        // 1. Explicit --app takes highest priority
        if (!string.IsNullOrWhiteSpace(opts.AppPath))
            return Path.GetFullPath(opts.AppPath);

        // 2. Reuse package root resolution (same as ensure-running)
        var packageRoot = ResolvePackageRootFromBase(baseDir);

        // 3. Portable package layout: <package>/AgentRecorder.App/AgentRecorder.App.exe
        var portablePaths = new[]
        {
            Path.Combine(packageRoot, "AgentRecorder.App"),
            baseDir,
            Path.Combine(Directory.GetParent(baseDir)?.FullName ?? baseDir, "AgentRecorder.App"),
        };

        foreach (var dir in portablePaths)
        {
            var path = Path.Combine(dir, "AgentRecorder.App.exe");
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        // 4. Source tree build output: walk up to find repo root (AgentRecorder.sln),
        //    then construct src/AgentRecorder.App/bin/<Config>/<tfm>/AgentRecorder.App.exe
        var sourceTreePath = FindSourceTreeAppPath(baseDir);
        if (sourceTreePath != null && File.Exists(sourceTreePath))
            return Path.GetFullPath(sourceTreePath);

        // 5. Fallback: return a path even if not found. status won't fail,
        //    but enable will report app_not_found.
        return Path.Combine(baseDir, "AgentRecorder.App.exe");
    }

    /// <summary>
    /// Walks up from baseDir to find the repo root (where AgentRecorder.sln is),
    /// then constructs the path to AgentRecorder.App.exe in the source tree build output.
    /// </summary>
    private static string? FindSourceTreeAppPath(string baseDir)
    {
        // baseDir is like: .../tools/AgentRecorder.Cli/bin/Release/<tfm>/
        // We need:   .../src/AgentRecorder.App/bin/Release/<tfm>/AgentRecorder.App.exe

        // Extract config and tfm from baseDir
        var binIdx = baseDir.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase);
        if (binIdx < 0) return null;

        var afterBin = baseDir.Substring(binIdx + 5); // e.g. "Release\net8.0-windows10.0.19041.0"
        var slashIdx = afterBin.IndexOf('\\');
        if (slashIdx <= 0) return null;

        var config = afterBin.Substring(0, slashIdx);
        var tfm = afterBin.Substring(slashIdx + 1).TrimEnd('\\', '/');

        // Walk up to find repo root
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentRecorder.sln")))
            {
                var appPath = Path.Combine(dir.FullName, "src", "AgentRecorder.App", "bin", config, tfm, "AgentRecorder.App.exe");
                return appPath;
            }
            dir = dir.Parent;
        }

        return null;
    }

    private static string ResolvePackageRootFromBase(string baseDir)
    {
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "AgentRecorder.App")) ||
                Directory.Exists(Path.Combine(dir.FullName, "AgentRecorder.Cli")) ||
                File.Exists(Path.Combine(dir.FullName, "AgentRecorder.App.exe")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return baseDir;
    }

    internal static AutoStartOptions ParseAutoStartOpts(string[] args, int startIdx)
    {
        var opts = new AutoStartOptions();
        for (int i = startIdx; i < args.Length; i++)
        {
            var arg = args[i];
            try
            {
                switch (arg)
                {
                    case "--json":
                        opts.Json = true;
                        break;
                    case "--app":
                        opts.AppPath = GetArgValue(args, ref i, "--app");
                        break;
                    case "--value-name":
                        opts.ValueName = GetArgValue(args, ref i, "--value-name");
                        break;
                    case "--help":
                    case "-h":
                        opts.ShowHelp = true;
                        break;
                    default:
                        throw new FormatException($"Unknown option: {arg}");
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
            {
                if (opts.ParseError == null)
                    opts.ParseError = ex.Message;
            }
        }
        return opts;
    }

    private static void WriteAutoStartJson(AutoStartResult result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        }));
    }

    private static void PrintAutoStartHelp()
    {
        Console.WriteLine("autostart - Manage Agent Recorder per-user autostart setting");
        Console.WriteLine();
        Console.WriteLine("Query or change whether Agent Recorder starts automatically on user login.");
        Console.WriteLine("Uses HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run registry key.");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  status    Show current autostart status (read-only, no registry changes)");
        Console.WriteLine("  enable    Enable autostart for the current user");
        Console.WriteLine("  disable   Disable autostart for the current user");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json              Output result as JSON (recommended for AI agents)");
        Console.WriteLine("  --app <path>        Path to AgentRecorder.App.exe (default: auto-detect)");
        Console.WriteLine("  --value-name <n>    Custom registry value name (for testing)");
        Console.WriteLine("  --help, -h          Show this help");
    }

    internal static EnsureRunningResult EnsureRunningCore(CliOptions opts)
    {
        var packageRoot = ResolvePackageRoot(opts);
        var dataDir = ResolveDataDir(opts, packageRoot);
        var readyPath = Path.Combine(dataDir, "runtime", "ready.json");

        var decision = EvaluateStaleReadyDecision(readyPath);

        switch (decision.Action)
        {
            case StaleReadyDecisionAction.ReuseExisting:
                return BuildSuccessResult(decision.Existing!, "existing", decision.ApiVersion!);

            case StaleReadyDecisionAction.ReturnError:
                return new EnsureRunningResult
                {
                    Ok = false,
                    Status = "error",
                    Code = decision.ErrorCode!,
                    Message = decision.Message!,
                    SuggestedAction = decision.SuggestedAction
                };

            case StaleReadyDecisionAction.DeleteFailed:
                return new EnsureRunningResult
                {
                    Ok = false,
                    Status = "error",
                    Code = "STALE_READY_FILE_DELETE_FAILED",
                    Message = $"Stale ready file exists at {readyPath} but could not be deleted: {decision.Message}",
                    SuggestedAction = $"Delete {readyPath} manually and try again."
                };

            case StaleReadyDecisionAction.ProceedToStart:
            default:
                break;
        }

        string exePath = ResolveServiceExe(opts, packageRoot);
        if (string.IsNullOrEmpty(exePath))
        {
            return new EnsureRunningResult
            {
                Ok = false,
                Status = "error",
                Code = "SERVICE_NOT_FOUND",
                Message = "Could not find AgentRecorder.App.exe or AgentRecorder.Headless.exe.",
                SuggestedAction = "Ensure you are running from the correct package root, or specify --app <path>."
            };
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
        };

        psi.Environment["AGENT_RECORDER_DATA_DIR"] = dataDir;

        var stopwatch = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start service process.");

        var namedEventName = RuntimeReadiness.NamedEventName;
        bool readySignaled = false;

        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(namedEventName);
            try { readyEvent.Reset(); } catch { }

            var remaining = Math.Max(0, opts.TimeoutMs - (int)stopwatch.ElapsedMilliseconds);
            readySignaled = readyEvent.WaitOne(remaining);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }

        if (!readySignaled)
        {
            readySignaled = WaitForReadyFile(readyPath, opts.TimeoutMs, stopwatch);
        }

        var final = ReadReadySnapshot(readyPath);
        if (final != null && IsAgentRecorderProcess(final.Pid))
        {
            var validation = ValidateReadySnapshot(final);
            if (validation.Valid)
            {
                return BuildSuccessResult(final, "started", validation.ApiVersion);
            }

            if (validation.ErrorCode == "STALE_READY_FILE")
            {
                return new EnsureRunningResult
                {
                    Ok = false,
                    Status = "error",
                    Code = "STALE_READY_FILE",
                    Message = validation.Message ?? "Ready file identity does not match /capabilities response after startup.",
                    SuggestedAction = "The ready.json file does not match the running service. Check for conflicting instances."
                };
            }
        }

        if (proc.HasExited)
        {
            return new EnsureRunningResult
            {
                Ok = false,
                Status = "error",
                Code = "SERVICE_EXITED",
                Message = $"Service process exited early with code {proc.ExitCode}.",
                SuggestedAction = $"Check audit log at {Path.Combine(dataDir, "logs", "audit.jsonl")}"
            };
        }

        return new EnsureRunningResult
        {
            Ok = false,
            Status = "error",
            Code = "READY_TIMEOUT",
            Message = $"Agent Recorder did not become ready within {opts.TimeoutMs / 1000} seconds.",
            SuggestedAction = "Check whether AgentRecorder.App.exe can start in the current desktop session."
        };
    }

    internal enum StaleReadyDecisionAction
    {
        ReuseExisting,
        ReturnError,
        DeleteFailed,
        ProceedToStart
    }

    internal sealed class StaleReadyDecision
    {
        public StaleReadyDecisionAction Action { get; set; }
        public ReadySnapshot? Existing { get; set; }
        public string? ApiVersion { get; set; }
        public string? ErrorCode { get; set; }
        public string? Message { get; set; }
        public string? SuggestedAction { get; set; }
    }

    internal sealed class StaleReadyDecisionContext
    {
        public Func<string, ReadySnapshot?> ReadReadySnapshot { get; set; } = Program.ReadReadySnapshot;
        public Func<bool> IsMutexHeld { get; set; } = () => Program.IsMutexHeld(SingleInstanceGuard.MutexName);
        public Func<int, bool> IsAgentRecorderProcess { get; set; } = Program.IsAgentRecorderProcess;
        public Func<ReadySnapshot, CapabilitiesValidation> ValidateReadySnapshot { get; set; } = Program.ValidateReadySnapshot;
        public Func<string, (bool Ok, string? Error)> DeleteReadyFile { get; set; } = path =>
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        };
    }

    internal static StaleReadyDecision EvaluateStaleReadyDecision(string readyPath)
    {
        return EvaluateStaleReadyDecision(readyPath, new StaleReadyDecisionContext());
    }

    internal static StaleReadyDecision EvaluateStaleReadyDecision(string readyPath, StaleReadyDecisionContext context)
    {
        var existing = context.ReadReadySnapshot(readyPath);
        bool mutexHeld = context.IsMutexHeld();
        bool isAgentProc = existing != null && context.IsAgentRecorderProcess(existing.Pid);

        if (existing == null)
        {
            if (mutexHeld)
            {
                return new StaleReadyDecision
                {
                    Action = StaleReadyDecisionAction.ReturnError,
                    ErrorCode = "INSTANCE_ALREADY_RUNNING_BUT_UNHEALTHY",
                    Message = "An Agent Recorder instance is already running (mutex held), but no healthy ready file was found in the current data-dir.",
                    SuggestedAction = "Use the same package/data-dir as the running instance, or stop the existing instance and try again."
                };
            }
            return new StaleReadyDecision { Action = StaleReadyDecisionAction.ProceedToStart };
        }

        if (isAgentProc)
        {
            var validation = context.ValidateReadySnapshot(existing);
            if (validation.Valid)
            {
                return new StaleReadyDecision
                {
                    Action = StaleReadyDecisionAction.ReuseExisting,
                    Existing = existing,
                    ApiVersion = validation.ApiVersion
                };
            }

            if (validation.ErrorCode == "STALE_READY_FILE")
            {
                if (mutexHeld)
                {
                    return new StaleReadyDecision
                    {
                        Action = StaleReadyDecisionAction.ReturnError,
                        ErrorCode = "CAPABILITIES_IDENTITY_MISMATCH",
                        Message = validation.Message ?? "Ready file identity does not match /capabilities response, and another instance holds the mutex.",
                        SuggestedAction = "Use the correct data-dir that matches the running instance, or stop the existing instance and try again."
                    };
                }

                var deleteResult = context.DeleteReadyFile(readyPath);
                if (!deleteResult.Ok)
                {
                    return new StaleReadyDecision
                    {
                        Action = StaleReadyDecisionAction.DeleteFailed,
                        Message = deleteResult.Error
                    };
                }
                return new StaleReadyDecision { Action = StaleReadyDecisionAction.ProceedToStart };
            }

            if (mutexHeld)
            {
                return new StaleReadyDecision
                {
                    Action = StaleReadyDecisionAction.ReturnError,
                    ErrorCode = "INSTANCE_ALREADY_RUNNING_BUT_UNHEALTHY",
                    Message = validation.Message ?? "Running AgentRecorder instance is unhealthy (no valid /capabilities) and mutex is held.",
                    SuggestedAction = "Check whether the existing instance is still responding, or stop it and try again."
                };
            }

            var delResult = context.DeleteReadyFile(readyPath);
            if (!delResult.Ok)
            {
                return new StaleReadyDecision
                {
                    Action = StaleReadyDecisionAction.DeleteFailed,
                    Message = delResult.Error
                };
            }
            return new StaleReadyDecision { Action = StaleReadyDecisionAction.ProceedToStart };
        }

        if (mutexHeld)
        {
            return new StaleReadyDecision
            {
                Action = StaleReadyDecisionAction.ReturnError,
                ErrorCode = "STALE_READY_FILE",
                Message = "Ready file exists but PID is not an Agent Recorder process, and another instance holds the mutex.",
                SuggestedAction = "Use the same package/data-dir as the running instance, or stop the existing instance and try again."
            };
        }

        var deleteResult2 = context.DeleteReadyFile(readyPath);
        if (!deleteResult2.Ok)
        {
            return new StaleReadyDecision
            {
                Action = StaleReadyDecisionAction.DeleteFailed,
                Message = deleteResult2.Error
            };
        }
        return new StaleReadyDecision { Action = StaleReadyDecisionAction.ProceedToStart };
    }

    private static string ResolvePackageRoot(CliOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.PackageRoot))
        {
            return Path.GetFullPath(opts.PackageRoot);
        }

        // Walk up from base directory looking for AgentRecorder.App or AgentRecorder.Cli dir
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "AgentRecorder.App")) ||
                Directory.Exists(Path.Combine(dir.FullName, "AgentRecorder.Cli")) ||
                File.Exists(Path.Combine(dir.FullName, "AgentRecorder.App.exe")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return baseDir;
    }

    internal static string ResolveDataDirForTest(CliOptions opts, string packageRoot) =>
        ResolveDataDir(opts, packageRoot);

    private static string ResolveDataDir(CliOptions opts, string packageRoot)
    {
        if (!string.IsNullOrWhiteSpace(opts.DataDir))
        {
            return Path.GetFullPath(opts.DataDir);
        }

        return Path.GetFullPath(Path.Combine(packageRoot, ".local-data"));
    }

    private static string ResolveServiceExe(CliOptions opts, string packageRoot)
    {
        // Explicit --app takes highest priority
        if (!string.IsNullOrWhiteSpace(opts.AppPath))
        {
            var path = Path.GetFullPath(opts.AppPath);
            if (File.Exists(path)) return path;
            return string.Empty;
        }

        var baseDir = AppContext.BaseDirectory;
        var parentDir = Directory.GetParent(baseDir)?.FullName ?? baseDir;

        var searchPaths = new[]
        {
            Path.Combine(packageRoot, "AgentRecorder.App"),
            Path.Combine(packageRoot, "AgentRecorder.Headless"),
            baseDir,
            Path.Combine(parentDir, "AgentRecorder.App"),
            Path.Combine(parentDir, "AgentRecorder.Headless"),
            Path.Combine(parentDir, "..", "AgentRecorder.App"),
            Path.Combine(parentDir, "..", "AgentRecorder.Headless")
        };

        string FindExe(string exeName)
        {
            foreach (var dir in searchPaths)
            {
                var path = Path.Combine(dir, exeName);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
            return string.Empty;
        }

        // If --headless is explicitly specified, prefer headless
        if (opts.PreferHeadless)
        {
            var headless = FindExe("AgentRecorder.Headless.exe");
            if (!string.IsNullOrEmpty(headless)) return headless;
        }

        // Default: prefer App (tray) - supports local selection/confirmation UI
        var defaultApp = FindExe("AgentRecorder.App.exe");
        if (!string.IsNullOrEmpty(defaultApp)) return defaultApp;

        // Fallback to headless if App is not available
        var fallbackHeadless = FindExe("AgentRecorder.Headless.exe");
        if (!string.IsNullOrEmpty(fallbackHeadless)) return fallbackHeadless;

        return string.Empty;
    }

    private static ReadySnapshot? ReadReadySnapshot(string readyPath)
    {
        try
        {
            if (!File.Exists(readyPath)) return null;
            var json = File.ReadAllText(readyPath);
            var snap = JsonSerializer.Deserialize<ReadySnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
            return snap;
        }
        catch
        {
            return null;
        }
    }

    private static bool WaitForReadyFile(string readyPath, int timeoutMs, Stopwatch stopwatch)
    {
        var pollInterval = 200;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            var snap = ReadReadySnapshot(readyPath);
            if (snap != null && snap.Ready && IsAgentRecorderProcess(snap.Pid))
            {
                return true;
            }
            Thread.Sleep(pollInterval);
        }
        return false;
    }

    private static bool IsAgentRecorderProcess(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.HasExited) return false;

            var processName = proc.ProcessName.ToLowerInvariant();
            if (processName.Contains("agentrecorder")) return true;

            try
            {
                var fileName = proc.MainModule?.FileName ?? string.Empty;
                if (fileName.IndexOf("AgentRecorder.App", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    fileName.IndexOf("AgentRecorder.Headless", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
                // May not have access to MainModule - fall back to process name check
            }

            return false;
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

    private static bool IsMutexHeld(string mutexName)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(mutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static CapabilitiesValidation ValidateReadySnapshot(ReadySnapshot snap)
    {
        if (snap.Port <= 0)
            return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Invalid port in ready file." };

        try
        {
            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
            var url = $"http://127.0.0.1:{snap.Port}/api/v1/capabilities";
            var response = http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = $"HTTP {(int)response.StatusCode} from /capabilities." };

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return ValidateReadySnapshotAgainstCapabilitiesJson(snap, json);
        }
        catch
        {
            return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Failed to reach /capabilities endpoint." };
        }
    }

    /// <summary>
    /// Validates that the /capabilities JSON response's readiness object
    /// matches the identity fields in the ready.json snapshot.
    /// This method is testable without a real HTTP server.
    /// </summary>
    internal static CapabilitiesValidation ValidateReadySnapshotAgainstCapabilitiesJson(ReadySnapshot snap, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean() == false)
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Capabilities returned ok=false." };

            if (!root.TryGetProperty("data", out var dataProp))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Capabilities response missing data field." };

            if (!dataProp.TryGetProperty("readiness", out var readinessProp))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Capabilities response missing data.readiness field." };

            // Check readiness.ready
            if (!readinessProp.TryGetProperty("ready", out var readyProp) || !readyProp.GetBoolean())
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Service readiness.ready is false or missing." };

            // Check pid - required field
            if (!readinessProp.TryGetProperty("pid", out var pidProp))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Missing required readiness identity field: pid" };
            if (pidProp.GetInt32() != snap.Pid)
                return new CapabilitiesValidation { Valid = false, ErrorCode = "STALE_READY_FILE", Message = $"PID mismatch: ready.json has {snap.Pid}, capabilities has {pidProp.GetInt32()}." };

            // Check port - required field
            if (!readinessProp.TryGetProperty("port", out var portProp))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Missing required readiness identity field: port" };
            if (portProp.GetInt32() != snap.Port)
                return new CapabilitiesValidation { Valid = false, ErrorCode = "STALE_READY_FILE", Message = $"Port mismatch: ready.json has {snap.Port}, capabilities has {portProp.GetInt32()}." };

            // Check mode - required field (case-insensitive)
            if (!readinessProp.TryGetProperty("mode", out var modeProp))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Missing required readiness identity field: mode" };
            var capMode = modeProp.GetString() ?? "";
            if (!string.Equals(capMode, snap.Mode, StringComparison.OrdinalIgnoreCase))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "STALE_READY_FILE", Message = $"Mode mismatch: ready.json has '{snap.Mode}', capabilities has '{capMode}'." };

            // Check ready_file - required field (path normalized, case-insensitive on Windows)
            if (!readinessProp.TryGetProperty("ready_file", out var readyFileProp))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Missing required readiness identity field: ready_file" };
            var capReadyFile = readyFileProp.GetString() ?? "";
            if (!PathsEqual(capReadyFile, snap.ReadyFile))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "STALE_READY_FILE", Message = $"ready_file mismatch: ready.json has '{snap.ReadyFile}', capabilities has '{capReadyFile}'." };

            // Check api_key_file - required field (path normalized, case-insensitive on Windows)
            if (!readinessProp.TryGetProperty("api_key_file", out var apiKeyFileProp))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Missing required readiness identity field: api_key_file" };
            var capApiKeyFile = apiKeyFileProp.GetString() ?? "";
            if (!PathsEqual(capApiKeyFile, snap.ApiKeyFile))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "STALE_READY_FILE", Message = $"api_key_file mismatch: ready.json has '{snap.ApiKeyFile}', capabilities has '{capApiKeyFile}'." };

            // Check data_dir - required field (path normalized, case-insensitive on Windows)
            if (!readinessProp.TryGetProperty("data_dir", out var dataDirProp))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = "Missing required readiness identity field: data_dir" };
            var capDataDir = dataDirProp.GetString() ?? "";
            if (!PathsEqual(capDataDir, snap.DataDir))
                return new CapabilitiesValidation { Valid = false, ErrorCode = "STALE_READY_FILE", Message = $"data_dir mismatch: ready.json has '{snap.DataDir}', capabilities has '{capDataDir}'." };

            // Get api_version
            var apiVersion = "v1";
            if (readinessProp.TryGetProperty("api_version", out var apiVerProp))
                apiVersion = apiVerProp.GetString() ?? "v1";

            return new CapabilitiesValidation { Valid = true, ApiVersion = apiVersion };
        }
        catch (Exception ex)
        {
            return new CapabilitiesValidation { Valid = false, ErrorCode = "CAPABILITIES_UNAVAILABLE", Message = $"Failed to parse capabilities response: {ex.Message}" };
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            var na = Path.GetFullPath(a);
            var nb = Path.GetFullPath(b);
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static EnsureRunningResult BuildSuccessResult(ReadySnapshot snap, string source, string apiVersion)
    {
        return new EnsureRunningResult
        {
            Ok = true,
            Status = "ready",
            Started = source == "started",
            Source = source,
            Mode = snap.Mode,
            Pid = snap.Pid,
            Port = snap.Port,
            ApiVersion = apiVersion,
            StartedAt = snap.StartedAt,
            ReadyAt = snap.ReadyAt,
            StartupElapsedMs = snap.StartupElapsedMs,
            ReadyFile = snap.ReadyFile,
            ApiKeyFile = snap.ApiKeyFile,
            DataDir = snap.DataDir,
            AuditLogPath = snap.AuditLogPath,
            NamedEvent = snap.NamedEvent
        };
    }

    private static int RunHelp()
    {
        PrintUsage();
        return 0;
    }

    private static int RunUnknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine();
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AgentRecorder.Cli - Agent startup handshake tool");
        Console.WriteLine();
        Console.WriteLine("Usage: AgentRecorder.Cli <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  ensure-running   Ensure Agent Recorder is running and return readiness info");
        Console.WriteLine("  autostart        Manage per-user autostart (login startup) setting");
        Console.WriteLine("  help             Show this help");
        Console.WriteLine();
        Console.WriteLine("Run 'AgentRecorder.Cli ensure-running --help' for command-specific options.");
    }

    private static void PrintEnsureRunningHelp()
    {
        Console.WriteLine("ensure-running - Ensure Agent Recorder is running and return readiness info");
        Console.WriteLine();
        Console.WriteLine("Checks if Agent Recorder is already running. If not, starts a new instance.");
        Console.WriteLine("Outputs machine-readable JSON with connection details when --json is used.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json                    Output result as JSON (recommended for AI agents)");
        Console.WriteLine("  --verbose                 Output human-readable diagnostic information");
        Console.WriteLine("  --package-root <path>     Portable package root directory");
        Console.WriteLine("  --app <path>              Path to AgentRecorder.App.exe");
        Console.WriteLine("  --data-dir <path>         Data directory (default: <package-root>\\.local-data)");
        Console.WriteLine("  --timeout-seconds <n>     Max seconds to wait for readiness (default: 30)");
        Console.WriteLine("  --timeout-ms <ms>         Max milliseconds to wait for readiness");
        Console.WriteLine("  --headless                Start in headless mode (advanced)");
        Console.WriteLine("  --tray                    Start in tray (GUI) mode (default)");
        Console.WriteLine("  --help, -h                Show this help");
    }
}

internal sealed class CliOptions
{
    public bool Json { get; set; }
    public bool Verbose { get; set; }
    public int TimeoutMs { get; set; }
    public int TimeoutSeconds { get; set; }
    public bool PreferHeadless { get; set; }
    public bool PreferTray { get; set; }
    public string? DataDir { get; set; }
    public string? PackageRoot { get; set; }
    public string? AppPath { get; set; }
    public bool ShowHelp { get; set; }
    public string? ParseError { get; set; }
}

internal sealed class EnsureRunningResult
{
    public bool Ok { get; set; }
    public string Status { get; set; } = "";
    public bool Started { get; set; }
    public string? Source { get; set; }
    public string Mode { get; set; } = "";
    public int Pid { get; set; }
    public int Port { get; set; }
    public string ApiVersion { get; set; } = "";
    public string? StartedAt { get; set; }
    public string? ReadyAt { get; set; }
    public long StartupElapsedMs { get; set; }
    public string ReadyFile { get; set; } = "";
    public string ApiKeyFile { get; set; } = "";
    public string DataDir { get; set; } = "";
    public string? AuditLogPath { get; set; }
    public string? NamedEvent { get; set; }

    // Error fields
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? SuggestedAction { get; set; }
}

internal sealed class CapabilitiesValidation
{
    public bool Valid { get; set; }
    public string ApiVersion { get; set; } = "v1";
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}

internal sealed class AutoStartOptions
{
    public bool Json { get; set; }
    public string? AppPath { get; set; }
    public string? ValueName { get; set; }
    public bool ShowHelp { get; set; }
    public string? ParseError { get; set; }
}

internal sealed class AutoStartResult
{
    public bool Ok { get; set; }
    public string Status { get; set; } = "";
    public bool Enabled { get; set; }
    public bool MatchesCurrentApp { get; set; }
    public string ValueName { get; set; } = "";
    public string RunKey { get; set; } = "";
    public string AppPath { get; set; } = "";
    public string? ConfiguredCommand { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? SuggestedAction { get; set; }
}
