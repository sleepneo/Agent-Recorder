using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>Frozen facts for retrieval, never a permanent HWND generation or authorization credential.</summary>
internal static class OrdinaryWindowTargetKey
{
    internal const string Prefix = "target:v1:window:";
    internal static bool IsValid(string? key) => key is not null && key.StartsWith(Prefix, StringComparison.Ordinal) &&
        key.Length == Prefix.Length + 64 && key.AsSpan(Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static string? Create(CapturePlan? plan, WindowProcessMetadata? observed)
    {
        if (plan is null || observed is null || plan.SourceKind != "window" || plan.CaptureSemantics != "window_surface" ||
            plan.WindowHandle == 0 || observed.WindowHandle != plan.WindowHandle ||
            plan.TargetWindowProcessId is not > 0 || observed.ProcessId != plan.TargetWindowProcessId ||
            observed.CreationFileTimeUtc is <= 0 or > 2650467743999999999 || observed.SessionId < 0 ||
            plan.TargetWindowSurfaceBounds is not { Width: > 0, Height: > 0 } surface) return null;
        using var stream = new MemoryStream();
        void Field(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            stream.Write(bytes);
        }
        void Number(long value) => Field(value.ToString(CultureInfo.InvariantCulture));
        Field("ordinary-window-target:v1"); Field("window"); Field("window_surface");
        // HWND is encoded as unsigned pointer bits, invariant decimal, independent of signed window_id text.
        Field(unchecked((nuint)plan.WindowHandle).ToString(CultureInfo.InvariantCulture));
        Number(observed.ProcessId); Number(observed.CreationFileTimeUtc); Number(observed.SessionId);
        Number(surface.Width); Number(surface.Height);
        return Prefix + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
}
