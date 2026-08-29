using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Windows;

/// <summary>
/// Reliability state for the internal display identity. The raw device path
/// used to derive a resolved identity is never part of this result.
/// </summary>
public enum DisplayIdentityResolutionStatus
{
    Resolved,
    Unavailable,
    Missing,
    Unresolved,
    Ambiguous,
    Conflict
}

/// <summary>
/// Parses the Windows display number exposed by the GDI monitor device name.
/// This is deliberately separate from Agent Recorder's public display token:
/// the latter is assigned from the current enumeration order for compatibility,
/// while this value comes from the complete <c>\\.\DISPLAY&lt;N&gt;</c> device name.
/// </summary>
public static class WindowsDisplayNumberParser
{
    private const string GdiDevicePrefix = @"\\.\DISPLAY";

    /// <summary>
    /// Parses only a complete, case-insensitive GDI device name such as
    /// <c>\\.\DISPLAY1</c>. The suffix must be ASCII decimal digits for a
    /// positive Int32; signs, whitespace, suffixes, zero, overflow, and
    /// approximate names are rejected.
    /// </summary>
    public static bool TryParse(string? deviceName, out int number)
    {
        number = 0;
        if (string.IsNullOrEmpty(deviceName) ||
            deviceName.Length <= GdiDevicePrefix.Length ||
            !deviceName.StartsWith(GdiDevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = deviceName[GdiDevicePrefix.Length..];
        foreach (var character in suffix)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return int.TryParse(
                   suffix,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out number)
            && number > 0;
    }
}

/// <summary>
/// One active DisplayConfig source-to-target association. This is an internal
/// topology-reader input; callers must not serialize or log it.
/// </summary>
internal sealed record DisplayTargetMapping(
    string SourceName,
    string? TargetDevicePath,
    bool PathActive = true,
    bool TargetAvailable = true,
    bool TargetInUse = true,
    bool SourceDeviceInfoAvailable = true,
    bool TargetDeviceInfoAvailable = true);

/// <summary>
/// Privacy-safe result of resolving one active source to its target identity.
/// A resolved value is a fixed-format SHA-256 fingerprint only.
/// </summary>
public sealed record DisplayIdentityResolution(
    string? Fingerprint,
    DisplayIdentityResolutionStatus Status);

/// <summary>
/// Derives stable display identity from active target-device material. It is
/// deliberately independent of monitor enumeration order, public display IDs,
/// bounds, primary status, names, and HMONITOR values.
/// </summary>
public static class DisplayIdentityDeriver
{
    public const string FingerprintPrefix = "display-stable-v1-";
    public const int FingerprintHexLength = 64;
    public const int FingerprintLength = 82;

    internal static DisplayIdentityResolution Resolve(
        string? sourceName,
        IEnumerable<DisplayTargetMapping>? mappings)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return Unresolved();

        var all = (mappings ?? Array.Empty<DisplayTargetMapping>()).ToArray();
        var normalizedSource = Normalize(sourceName);
        var matching = all
            .Where(mapping => mapping != null && Normalize(mapping.SourceName) == normalizedSource)
            .ToArray();

        if (matching.Length == 0)
            return new(null, DisplayIdentityResolutionStatus.Missing);

        if (matching.Any(mapping =>
                !mapping.PathActive ||
                !mapping.TargetAvailable ||
                !mapping.TargetInUse ||
                !mapping.SourceDeviceInfoAvailable ||
                !mapping.TargetDeviceInfoAvailable))
        {
            return new(null, DisplayIdentityResolutionStatus.Unavailable);
        }

        if (matching.Any(mapping => string.IsNullOrWhiteSpace(mapping.TargetDevicePath)))
            return Unresolved();

        var normalizedPaths = matching
            .Select(mapping => Normalize(mapping.TargetDevicePath!))
            .ToArray();

        // An identical target appearing twice is not a valid clone set. It is
        // evidence that the active topology was duplicated or parsed twice.
        if (normalizedPaths.Length != normalizedPaths.Distinct(StringComparer.Ordinal).Count())
            return new(null, DisplayIdentityResolutionStatus.Ambiguous);

        // A target path cannot simultaneously identify two different active
        // sources. This catches conflicting source-name mappings.
        foreach (var target in normalizedPaths)
        {
            var sourceCount = all
                .Where(mapping => mapping != null &&
                    !string.IsNullOrWhiteSpace(mapping.TargetDevicePath) &&
                    Normalize(mapping.TargetDevicePath!) == target)
                .Select(mapping => Normalize(mapping.SourceName))
                .Distinct(StringComparer.Ordinal)
                .Count();
            if (sourceCount != 1)
                return new(null, DisplayIdentityResolutionStatus.Conflict);
        }

        // Clone targets are a set, not an array position. Sorting the normalized
        // material makes the same physical target set order-independent.
        var material = string.Join("\n", normalizedPaths.OrderBy(path => path, StringComparer.Ordinal));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            "AgentRecorder.DisplayIdentity.v1\0" + material));
        var fingerprint = FingerprintPrefix + Convert.ToHexString(bytes).ToLowerInvariant();
        return new(fingerprint, DisplayIdentityResolutionStatus.Resolved);
    }

    public static bool IsFixedFormat(string? fingerprint)
        => fingerprint?.Length == FingerprintLength
            && fingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal)
            && fingerprint[FingerprintPrefix.Length..].All(IsLowerHex);

    private static DisplayIdentityResolution Unresolved()
        => new(null, DisplayIdentityResolutionStatus.Unresolved);

    private static string Normalize(string value)
        => value.Trim().ToUpperInvariant();

    private static bool IsLowerHex(char value)
        => value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
