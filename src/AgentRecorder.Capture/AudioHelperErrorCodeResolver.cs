using System;
using System.Collections.Generic;

namespace AgentRecorder.Capture;

/// <summary>
/// Normalizes audio helper error codes to a stable, bounded allowlist.
/// Unknown or empty values are mapped to <c>audio_helper_failure</c> so
/// callers never surface free-text helper reasons as machine error codes.
/// </summary>
public static class AudioHelperErrorCodeResolver
{
    private static readonly HashSet<string> AllowedCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio_endpoint_not_found",
        "audio_endpoint_inactive",
        "audio_format_unsupported",
        "audio_output_conflict",
        "audio_writer_finalize_failed",
        "audio_write_failure",
        "audio_capture_error",
        "audio_no_packets_captured",
        "audio_publish_failed",
        "audio_helper_runtime_failure",
        "audio_helper_no_terminal_event",
        "audio_helper_exit_protocol_mismatch",
        "audio_helper_protocol_error"
    };

    /// <summary>
    /// Returns the canonical lowercase <paramref name="code"/> if it is in the
    /// known allowlist; returns <c>null</c> for null/whitespace inputs;
    /// returns <c>audio_helper_failure</c> for any unknown non-empty code.
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        foreach (var allowed in AllowedCodes)
        {
            if (AllowedCodes.Comparer.Equals(allowed, code))
                return allowed;
        }

        return "audio_helper_failure";
    }

    /// <summary>
    /// True when <paramref name="code"/> is a known, stable helper error code.
    /// </summary>
    public static bool IsAllowed(string? code)
    {
        return !string.IsNullOrWhiteSpace(code) && AllowedCodes.Contains(code);
    }
}
