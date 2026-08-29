namespace AgentRecorder.Capture;

/// <summary>
/// Trusted lifecycle abort reasons produced by application-owned runtime
/// supervision. These values are deliberately not parsed from client input or
/// arbitrary backend text.
/// </summary>
public enum CaptureAbortReason
{
    DisplayUnavailable = 1
}

public static class CaptureAbortReasonCodes
{
    public static string ToCode(CaptureAbortReason reason) => reason switch
    {
        CaptureAbortReason.DisplayUnavailable => "display_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown capture abort reason.")
    };
}
