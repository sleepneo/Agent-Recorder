namespace AgentRecorder.AudioHelper;

internal enum AudioHelperMode
{
    None,
    Capture,
    Probe,
    Version
}

internal enum AudioCaptureEngine
{
    WasapiDirect,
    WindowsMediaCapture
}

internal static class AudioCaptureEngineNames
{
    public const string WasapiDirect = "wasapi-direct";
    public const string WindowsMediaCapture = "windows-mediacapture";

    public static string ToCliValue(AudioCaptureEngine engine)
    {
        return engine == AudioCaptureEngine.WindowsMediaCapture
            ? WindowsMediaCapture
            : WasapiDirect;
    }
}

internal sealed class AudioHelperOptions
{
    public AudioHelperMode Mode { get; set; }
    public AudioSourceKind SourceKind { get; set; } = AudioSourceKind.Microphone;
    public AudioCaptureEngine CaptureEngine { get; set; } = AudioCaptureEngine.WasapiDirect;
    internal bool AutoHfpPairDiscovery { get; set; }
    public string EndpointId { get; set; } = "";
    public string HfpRenderEndpointId { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string AllowedRoot { get; set; } = "";
    public string StopSignalPath { get; set; } = "";
    public string RecordingId { get; set; } = "";
}

internal sealed class AudioHelperParseResult
{
    public bool Ok { get; set; }
    public string Error { get; set; } = "";
    public AudioHelperOptions Options { get; set; } = new();
}

internal static class AudioHelperArgumentParser
{
    private const int MaxArgValueLength = 4096;

    public static AudioHelperParseResult Parse(string[] args)
    {
        var result = new AudioHelperParseResult();
        var opts = result.Options;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg) || arg[0] != '-')
            {
                result.Error = $"Unexpected positional argument: {arg}";
                return result;
            }

            var name = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : arg[1..];

            if (!seen.Add(name))
            {
                result.Error = $"Duplicate argument: {arg}";
                return result;
            }

            if (string.Equals(name, "version", StringComparison.OrdinalIgnoreCase))
            {
                if (opts.Mode == AudioHelperMode.Probe)
                {
                    result.Error = "--probe and --version cannot be used together";
                    return result;
                }
                opts.Mode = AudioHelperMode.Version;
                continue;
            }

            if (string.Equals(name, "probe", StringComparison.OrdinalIgnoreCase))
            {
                if (opts.Mode == AudioHelperMode.Version)
                {
                    result.Error = "--probe and --version cannot be used together";
                    return result;
                }
                opts.Mode = AudioHelperMode.Probe;
                continue;
            }

            // Capture-mode arguments are not allowed once version/probe has been selected.
            if (opts.Mode == AudioHelperMode.Version)
            {
                result.Error = "--version cannot be mixed with capture arguments";
                return result;
            }
            if (opts.Mode == AudioHelperMode.Probe)
            {
                result.Error = "--probe cannot be mixed with capture arguments";
                return result;
            }

            if (string.Equals(name, "auto-hfp-pair", StringComparison.OrdinalIgnoreCase))
            {
                opts.AutoHfpPairDiscovery = true;
                continue;
            }

            string? TakeNext(string displayName)
            {
                if (i + 1 >= args.Length)
                {
                    result.Error = $"Missing value for argument --{displayName}";
                    return null;
                }
                var v = args[++i];
                if (string.IsNullOrWhiteSpace(v))
                {
                    result.Error = $"Empty value for argument --{displayName}";
                    return null;
                }
                if (v.Length > MaxArgValueLength)
                {
                    result.Error = $"Value for argument --{displayName} is too long";
                    return null;
                }
                return v;
            }

            if (string.Equals(name, "endpoint-id", StringComparison.OrdinalIgnoreCase))
            {
                var v = TakeNext("endpoint-id");
                if (v == null) return result;
                if (ContainsControlCharacter(v))
                {
                    result.Error = "--endpoint-id contains control characters";
                    return result;
                }
                opts.EndpointId = v;
            }
            else if (string.Equals(name, "hfp-render-endpoint-id", StringComparison.OrdinalIgnoreCase))
            {
                var v = TakeNext("hfp-render-endpoint-id");
                if (v == null) return result;
                if (ContainsControlCharacter(v))
                {
                    result.Error = "--hfp-render-endpoint-id contains control characters";
                    return result;
                }
                opts.HfpRenderEndpointId = v;
            }
            else if (string.Equals(name, "output", StringComparison.OrdinalIgnoreCase))
            {
                var v = TakeNext("output");
                if (v == null) return result;
                opts.OutputPath = v;
            }
            else if (string.Equals(name, "allowed-root", StringComparison.OrdinalIgnoreCase))
            {
                var v = TakeNext("allowed-root");
                if (v == null) return result;
                opts.AllowedRoot = v;
            }
            else if (string.Equals(name, "stop-signal", StringComparison.OrdinalIgnoreCase))
            {
                var v = TakeNext("stop-signal");
                if (v == null) return result;
                opts.StopSignalPath = v;
            }
            else if (string.Equals(name, "recording-id", StringComparison.OrdinalIgnoreCase))
            {
                var v = TakeNext("recording-id");
                if (v == null) return result;
                opts.RecordingId = v;
            }
            else if (string.Equals(name, "capture-engine", StringComparison.OrdinalIgnoreCase))
            {
                var v = TakeNext("capture-engine");
                if (v == null) return result;
                if (string.Equals(v, "wasapi-direct", StringComparison.OrdinalIgnoreCase))
                    opts.CaptureEngine = AudioCaptureEngine.WasapiDirect;
                else if (string.Equals(v, "windows-mediacapture", StringComparison.OrdinalIgnoreCase))
                    opts.CaptureEngine = AudioCaptureEngine.WindowsMediaCapture;
                else
                {
                    result.Error = $"Unknown capture engine: {v}";
                    return result;
                }
            }
            else if (string.Equals(name, "source-kind", StringComparison.OrdinalIgnoreCase))
            {
                var v = TakeNext("source-kind");
                if (v == null) return result;
                if (!AudioSourceKindNames.TryParse(v, out var sourceKind))
                {
                    result.Error = $"Unknown source kind: {v}";
                    return result;
                }
                opts.SourceKind = sourceKind;
            }
            else
            {
                result.Error = $"Unknown argument: {arg}";
                return result;
            }
        }

        if (opts.Mode == AudioHelperMode.None)
            opts.Mode = AudioHelperMode.Capture;

        if (opts.Mode == AudioHelperMode.Version || opts.Mode == AudioHelperMode.Probe)
        {
            if (!string.IsNullOrEmpty(opts.EndpointId) ||
                !string.IsNullOrEmpty(opts.HfpRenderEndpointId) ||
                !string.IsNullOrEmpty(opts.OutputPath) ||
                !string.IsNullOrEmpty(opts.AllowedRoot) ||
                !string.IsNullOrEmpty(opts.StopSignalPath) ||
                !string.IsNullOrEmpty(opts.RecordingId) ||
                opts.AutoHfpPairDiscovery ||
                seen.Contains("capture-engine") ||
                seen.Contains("source-kind"))
            {
                string modeName = opts.Mode == AudioHelperMode.Version ? "version" : "probe";
                result.Error = $"--{modeName} cannot be mixed with capture arguments";
                return result;
            }
        }

        if (!string.IsNullOrEmpty(opts.HfpRenderEndpointId) &&
            opts.CaptureEngine != AudioCaptureEngine.WasapiDirect)
        {
            result.Error = "--hfp-render-endpoint-id requires --capture-engine wasapi-direct";
            return result;
        }

        if (opts.AutoHfpPairDiscovery && opts.CaptureEngine != AudioCaptureEngine.WasapiDirect)
        {
            result.Error = "--auto-hfp-pair requires --capture-engine wasapi-direct";
            return result;
        }

        if (opts.SourceKind == AudioSourceKind.SystemLoopback)
        {
            if (opts.CaptureEngine != AudioCaptureEngine.WasapiDirect)
            {
                result.Error = "--source-kind system-loopback requires --capture-engine wasapi-direct";
                return result;
            }

            if (!string.IsNullOrEmpty(opts.HfpRenderEndpointId))
            {
                result.Error = "--hfp-render-endpoint-id cannot be used with --source-kind system-loopback";
                return result;
            }

            if (opts.AutoHfpPairDiscovery)
            {
                result.Error = "--auto-hfp-pair cannot be used with --source-kind system-loopback";
                return result;
            }
        }

        result.Ok = true;
        return result;
    }

    public static string? ValidateRecordingId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
            return "Recording id must be 1-64 characters.";
        foreach (var c in id)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
                return $"Recording id contains invalid character: {c}";
        }
        return null;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char c in value)
        {
            if (char.IsControl(c))
                return true;
        }
        return false;
    }
}
