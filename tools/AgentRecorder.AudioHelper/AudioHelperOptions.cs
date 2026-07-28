namespace AgentRecorder.AudioHelper;

internal enum AudioHelperMode
{
    None,
    Capture,
    Probe,
    Version
}

internal sealed class AudioHelperOptions
{
    public AudioHelperMode Mode { get; set; }
    public string EndpointId { get; set; } = "";
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
                !string.IsNullOrEmpty(opts.OutputPath) ||
                !string.IsNullOrEmpty(opts.AllowedRoot) ||
                !string.IsNullOrEmpty(opts.StopSignalPath) ||
                !string.IsNullOrEmpty(opts.RecordingId))
            {
                string modeName = opts.Mode == AudioHelperMode.Version ? "version" : "probe";
                result.Error = $"--{modeName} cannot be mixed with capture arguments";
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
