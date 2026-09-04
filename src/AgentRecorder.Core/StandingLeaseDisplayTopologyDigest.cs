using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Core.Automation;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>
/// Canonical metadata-only digest for the complete active display topology.
/// Public display ordinals are intentionally excluded so enumeration order
/// cannot make a stable topology appear changed.
/// </summary>
internal static class StandingLeaseDisplayTopologyDigest
{
    private const string Schema = "standing-display-topology/v1";

    internal static bool TryCompute(
        IReadOnlyList<StandingLeaseDisplayMetadata>? displays,
        out string digest)
    {
        digest = "";
        if (displays is null || displays.Count == 0)
            return false;

        var complete = displays
            .Where(display =>
                display.IdentityStatus == DisplayIdentityResolutionStatus.Resolved &&
                !string.IsNullOrWhiteSpace(display.StableDisplayFingerprint) &&
                display.PhysicalBounds is not null &&
                display.DpiX is > 0 &&
                display.DpiY is > 0 &&
                display.PhysicalWidth is > 0 &&
                display.PhysicalHeight is > 0 &&
                display.Orientation is not null)
            .ToArray();
        if (complete.Length != displays.Count)
            return false;

        var bytes = new List<byte>(512);
        AppendString(bytes, Schema);
        AppendInt32(bytes, complete.Length);
        foreach (var display in complete
                     .OrderBy(item => item.StableDisplayFingerprint, StringComparer.Ordinal)
                     .ThenBy(item => item.PhysicalBounds!.Value.X)
                     .ThenBy(item => item.PhysicalBounds!.Value.Y)
                     .ThenBy(item => item.PhysicalBounds!.Value.Width)
                     .ThenBy(item => item.PhysicalBounds!.Value.Height))
        {
            AppendString(bytes, display.StableDisplayFingerprint!);
            AppendRectangle(bytes, display.PhysicalBounds!.Value);
            AppendInt32(bytes, display.DpiX!.Value);
            AppendInt32(bytes, display.DpiY!.Value);
            AppendInt32(bytes, display.PhysicalWidth!.Value);
            AppendInt32(bytes, display.PhysicalHeight!.Value);
            AppendString(bytes, AuthorizedFixedRegionScopeCodes.ToCode(display.Orientation!.Value));
        }

        digest = Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
        return true;
    }

    private static void AppendRectangle(List<byte> bytes, AuthorizedPhysicalRectangle value)
    {
        AppendInt32(bytes, value.X);
        AppendInt32(bytes, value.Y);
        AppendInt32(bytes, value.Width);
        AppendInt32(bytes, value.Height);
    }

    private static void AppendString(List<byte> bytes, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        AppendInt32(bytes, utf8.Length);
        bytes.AddRange(utf8);
    }

    private static void AppendInt32(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }
}
