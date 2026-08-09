namespace AgentRecorder.Capture;

public enum WgcEncoderMode
{
    Software,
    HardwarePreferred
}

/// <summary>
/// Single normalization policy for the hidden WGC encoder experiment.
/// Empty or unset is explicitly software; every other value is rejected.
/// </summary>
public static class WgcEncoderModePolicy
{
    public const string EnvironmentVariable = "AGENT_RECORDER_WGC_ENCODER";

    public static WgcEncoderMode Normalize(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Equals("software", StringComparison.OrdinalIgnoreCase))
            return WgcEncoderMode.Software;
        if (normalized.Equals("hardware-preferred", StringComparison.OrdinalIgnoreCase))
            return WgcEncoderMode.HardwarePreferred;
        throw new ArgumentException(
            $"{EnvironmentVariable} must be software or hardware-preferred.", nameof(value));
    }

    public static WgcEncoderMode NormalizeEnvironment() =>
        Normalize(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static string ToArgumentValue(WgcEncoderMode mode) =>
        mode == WgcEncoderMode.HardwarePreferred ? "hardware-preferred" : "software";
}
