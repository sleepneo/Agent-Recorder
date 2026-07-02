namespace AgentRecorder.Capture;

/// <summary>
/// Parses the text protocol produced by wgc-native-helper.exe on
/// stdout/stderr into a structured WgcHelperResult.
///
/// The format is a simple series of "KEY: value" lines, one field per
/// line, with a two-space prefix or not.
/// </summary>
public static class WgcHelperOutputParser
{
    /// <summary>
    /// Parse helper output. Never throws — returns a result even on
    /// empty or malformed input; preserves RawStandardOutput /
    /// RawStandardError so callers can audit.
    /// </summary>
    public static WgcHelperResult Parse(int exitCode, string stdout, string stderr)
    {
        var result = new WgcHelperResult
        {
            ExitCode = exitCode,
            RawStandardOutput = stdout ?? "",
            RawStandardError = stderr ?? "",
        };

        // Parse stdout line by line; empty/blank/partial input never throws.
        // All fields are optional except RESULT which determines Success.
        if (!string.IsNullOrWhiteSpace(stdout))
        foreach (var rawLine in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimStart();
            if (line.Length == 0) continue;

            // Find first ": "
            int colonIdx = line.IndexOf(": ", StringComparison.Ordinal);
            if (colonIdx <= 0) continue; // skip non "key: value" lines (e.g. banners)

            var key = line.Substring(0, colonIdx).Trim();
            var value = line.Substring(colonIdx + 2);

            // Match keys (case-sensitive per contract)
            switch (key)
            {
                case "RESULT":
                    result.ResultToken = value.Trim();
                    break;
                case "Stage":
                    result.Stage = value.Trim();
                    break;
                case "HRESULT":
                    result.Hresult = value.Trim();
                    break;
                case "Reason":
                    result.Reason = value.Trim();
                    break;
                case "Output":
                    result.OutputPath = value.Trim();
                    break;
                case "Width":
                    if (int.TryParse(value.Trim(), out var w)) result.Width = w;
                    break;
                case "Height":
                    if (int.TryParse(value.Trim(), out var h)) result.Height = h;
                    break;
                case "FileSize":
                    result.FileSize = ParseFileSizeValue(value.Trim());
                    break;
                case "SHA-256":
                    result.Sha256 = value.Trim();
                    break;
                case "DisplayName":
                    result.DisplayName = value.Trim();
                    break;
                case "CaptureMethod":
                    result.CaptureMethod = value.Trim();
                    break;
                case "HWND":
                    result.Hwnd = value.Trim();
                    break;
                // Unknown fields: ignore explicitly
                default:
                    break;
            }
        }

        // Success: exit code == 0 AND RESULT == "OK"
        result.Success = (exitCode == 0) && string.Equals(result.ResultToken, "OK", StringComparison.Ordinal);

        return result;
    }

    /// <summary>Parses a FileSize value such as "123456", "123456 bytes" or "  123456   bytes".
    /// Returns 0 when no number can be extracted.</summary>
    private static long ParseFileSizeValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;

        int start = -1;
        int end = -1;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsAsciiDigit(c))
            {
                if (start == -1) start = i;
                end = i;
            }
            else if (start != -1)
            {
                break;
            }
        }

        if (start == -1) return 0;
        ReadOnlySpan<char> digits = value.AsSpan(start, end - start + 1);
        if (long.TryParse(digits, out var fs)) return fs;
        return 0;
    }
}
