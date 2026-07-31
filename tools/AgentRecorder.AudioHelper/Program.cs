using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace AgentRecorder.AudioHelper;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitArgumentError = 1;
    private const int ExitPathViolation = 2;
    private const int ExitEndpointNotFound = 3;
    private const int ExitEndpointInactive = 4;
    private const int ExitFormatUnsupported = 5;
    private const int ExitRuntimeFailure = 6;
    private const int ExitAlreadyExists = 7;

    internal static async Task<int> Main(string[] args)
    {
        var result = AudioHelperArgumentParser.Parse(args);
        if (!result.Ok)
        {
            WriteStderr(result.Error);
            return ExitArgumentError;
        }

        var opts = result.Options;
        if (opts.Mode == AudioHelperMode.Version)
        {
            WriteVersion();
            return ExitOk;
        }

        if (opts.Mode == AudioHelperMode.Probe)
        {
            // Probe only validates the helper is launchable and parses args.
            // It must not open the microphone or create any media file.
            WriteVersion();
            return ExitOk;
        }

        var validationError = ValidateCaptureArgs(opts);
        if (validationError != null)
        {
            WriteStderr(validationError);
            return ExitArgumentError;
        }

        if (!Path.IsPathRooted(opts.AllowedRoot))
        {
            WriteStderr("allowed-root must be an absolute path");
            return ExitArgumentError;
        }

        string allowedRoot;
        try
        {
            allowedRoot = Path.GetFullPath(opts.AllowedRoot);
        }
        catch
        {
            WriteStderr("allowed-root is invalid");
            return ExitArgumentError;
        }

        var policy = new PathPolicy(allowedRoot);
        var outputCheck = policy.ValidateOutputPath(opts.OutputPath);
        if (!outputCheck.Ok)
        {
            WriteStderr(outputCheck.Error);
            return MapPathErrorToExit(outputCheck.Error);
        }

        var stopCheck = policy.ValidateStopSignalPath(opts.StopSignalPath, outputCheck);
        if (!stopCheck.Ok)
        {
            WriteStderr(stopCheck.Error);
            return MapPathErrorToExit(stopCheck.Error);
        }

        // Ensure the parent directory exists. PathPolicy already requires it,
        // but guard against a TOCTOU race by recreating just before capture.
        string outputParent = Path.GetDirectoryName(outputCheck.CanonicalPath)!;
        try
        {
            Directory.CreateDirectory(outputParent);
        }
        catch (Exception ex)
        {
            WriteStderr("Failed to create output directory: " + ex.Message);
            return ExitRuntimeFailure;
        }

        string stopParent = Path.GetDirectoryName(stopCheck.CanonicalPath)!;
        try
        {
            Directory.CreateDirectory(stopParent);
        }
        catch (Exception ex)
        {
            WriteStderr("Failed to create stop-signal directory: " + ex.Message);
            return ExitRuntimeFailure;
        }

        var events = new EventWriter();
        using var cts = new CancellationTokenSource();
        var watcher = new StopWatcher(stopCheck.CanonicalPath, () => cts.Cancel());

        try
        {
            if (opts.CaptureEngine == AudioCaptureEngine.WindowsMediaCapture)
            {
                using var nativeSession = new NativeMediaCaptureSession(opts, outputCheck, events, watcher, cts);
                return await nativeSession.RunAsync().ConfigureAwait(false);
            }

            using var session = new CaptureSession(opts, outputCheck, events, watcher, cts);
            return session.Run();
        }
        catch (Exception ex)
        {
            WriteStderr("Unhandled session failure: " + ex.Message);
            return ExitRuntimeFailure;
        }
    }

    private static string? ValidateCaptureArgs(AudioHelperOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.EndpointId))
            return "--endpoint-id is required";
        if (string.IsNullOrWhiteSpace(opts.OutputPath))
            return "--output is required";
        if (string.IsNullOrWhiteSpace(opts.AllowedRoot))
            return "--allowed-root is required";
        if (string.IsNullOrWhiteSpace(opts.StopSignalPath))
            return "--stop-signal is required";
        if (string.IsNullOrWhiteSpace(opts.RecordingId))
            return "--recording-id is required";

        var idError = AudioHelperArgumentParser.ValidateRecordingId(opts.RecordingId);
        if (idError != null)
            return idError;

        if (opts.EndpointId.Length > 512)
            return "--endpoint-id is too long";

        return null;
    }

    private static int MapPathErrorToExit(string error)
    {
        if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            return ExitAlreadyExists;
        return ExitPathViolation;
    }

    private static void WriteVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetName().Name ?? "AgentRecorder.AudioHelper";
        var version = assembly.GetName().Version?.ToString() ?? "0.0.0";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
        Console.Out.WriteLine($"{name} {informational}");
        Console.Out.WriteLine($"Protocol: audio-helper-v1");
        Console.Out.WriteLine($"TimestampFrequency: {Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)}");
        Console.Out.Flush();
    }

    private static void WriteStderr(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
            // Best effort: stderr may be closed.
        }
    }
}
