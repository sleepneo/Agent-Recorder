using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Windows;

namespace AgentRecorder.Core;

/// <summary>A v1 frozen pixel target, never an authorization or physical identity proof.</summary>
public static class OrdinaryScreenTargetKey
{
    public static string? Create(CapturePlan? plan)
    {
        if (plan is null || !plan.HasExplicitResolvedDisplayIdentity ||
            plan.TargetDisplayIdentityStatus != DisplayIdentityResolutionStatus.Resolved ||
            plan.SourceKind is not ("display" or "region") ||
            plan.CaptureSemantics is not ("display_surface" or "region_rectangle" or "screen_rectangle") ||
            plan.SourceKind == "display" && plan.CaptureSemantics == "region_rectangle" ||
            plan.SourceKind == "region" && plan.CaptureSemantics == "display_surface" ||
            plan.CoordinateSpace != "virtual_screen" ||
            plan.TargetDisplayIdentity is not { Length: > 0 and <= 4096 } identity ||
            identity != identity.Trim() || identity.Contains('\0') ||
            !Legal(plan.DisplayBounds) || !Legal(plan.Bounds)) return null;
        var display = plan.DisplayBounds!;
        var bounds = plan.Bounds!;
        if (bounds.X < display.X || bounds.Y < display.Y ||
            (long)bounds.X + bounds.Width > (long)display.X + display.Width ||
            (long)bounds.Y + bounds.Height > (long)display.Y + display.Height) return null;
        var utf8 = new UTF8Encoding(false, true);
        try { _ = utf8.GetByteCount(identity); }
        catch (EncoderFallbackException) { return null; }
        // UTF-8 byte length, colon, bytes; fixed field order. Integers use invariant decimal.
        using var stream = new MemoryStream();
        void Field(string value)
        {
            var bytes = utf8.GetBytes(value);
            stream.Write(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            stream.Write(bytes);
        }
        void Rect(CapturePlanBounds rect)
        {
            Field(rect.X.ToString(CultureInfo.InvariantCulture)); Field(rect.Y.ToString(CultureInfo.InvariantCulture));
            Field(rect.Width.ToString(CultureInfo.InvariantCulture)); Field(rect.Height.ToString(CultureInfo.InvariantCulture));
        }
        Field("ordinary-screen-target:v1"); Field(plan.SourceKind); Field(plan.CaptureSemantics);
        Field(plan.CoordinateSpace); Field(identity); Rect(display); Rect(bounds);
        return "target:v1:" + plan.SourceKind + ":" + Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static bool Legal(CapturePlanBounds? rect) => rect is { Width: > 0, Height: > 0 } &&
        (long)rect.X + rect.Width <= int.MaxValue && (long)rect.Y + rect.Height <= int.MaxValue;

    public static bool IsValid(string? key)
    {
        if (key is null) return false;
        var prefix = key.StartsWith("target:v1:display:", StringComparison.Ordinal) ? "target:v1:display:" : "target:v1:region:";
        return key.StartsWith(prefix, StringComparison.Ordinal) && key.Length == prefix.Length + 64 &&
            key.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
    }
}
