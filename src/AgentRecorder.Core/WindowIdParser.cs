using System;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Core;

/// <summary>
/// Parses window_id strings (format: "window_<hwnd_value>") into HWND values,
/// and validates captured window metadata (minimized, zero HWND, etc.).
/// </summary>
public static class WindowIdParser
{
    /// <summary>
    /// Tries to parse a window_id string into a HWND.
    /// Format: "window_123456" → 123456 as nint.
    /// </summary>
    public static bool TryParse(string? windowId, out nint hwnd)
    {
        hwnd = default;
        if (string.IsNullOrWhiteSpace(windowId))
            return false;

        const string prefix = "window_";
        if (!windowId.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        string numericPart = windowId.Substring(prefix.Length);
        if (!long.TryParse(numericPart, out var hwndVal))
            return false;

        hwnd = new nint(hwndVal);
        return true;
    }

    /// <summary>
    /// Parses a window_id string, throwing an INVALID_ARGUMENT ApiException on failure.
    /// </summary>
    public static nint Parse(string? windowId)
    {
        if (!TryParse(windowId, out var hwnd))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                $"Invalid window_id: '{windowId}'. Expected format: 'window_<hwnd_value>'.");
        }

        if (hwnd == nint.Zero)
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "window_id resolved to HWND 0, which is invalid. Please pick a valid window.");
        }

        return hwnd;
    }

    /// <summary>
    /// Checks if a window is minimized. If so, throws SOURCE_UNAVAILABLE
    /// with a helpful message.
    /// </summary>
    public static void RejectMinimized(bool isMinimized, string windowTitle)
    {
        if (isMinimized)
        {
            throw new ApiException(403, "SOURCE_UNAVAILABLE",
                $"Window '{windowTitle}' is minimized. " +
                "WGC cannot capture minimized windows. " +
                "Restore the window first, then try again.",
                new { suggested_action = "restore_window_first" });
        }
    }
}
