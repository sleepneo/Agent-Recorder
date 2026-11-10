using System.Globalization;
using AgentRecorder.Capture;

namespace AgentRecorder.Core;

internal sealed class RecordingStorageReserve
{
    internal const string EnvironmentVariable = "AGENT_RECORDER_STORAGE_RESERVE_MIB";
    private const long MiB = 1024L * 1024;
    private const int MinimumMiB = (int)(WindowStorageSafety.LowWaterBytes / MiB);
    private const int MaximumMiB = 65536;

    private static readonly Lazy<RecordingStorageReserve> ProcessSetting = new(() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable)));

    internal static RecordingStorageReserve Startup => ProcessSetting.Value;
    internal long? EffectiveBytes { get; }
    internal string Source { get; }
    internal string? InvalidReason { get; }

    private RecordingStorageReserve(long? effectiveBytes, string source, string? invalidReason = null)
    {
        EffectiveBytes = effectiveBytes;
        Source = source;
        InvalidReason = invalidReason;
    }

    internal static RecordingStorageReserve Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return new(WindowStorageSafety.LowWaterBytes, "default");
        if (value.Any(c => c is < '0' or > '9'))
            return new(null, "environment", "invalid_integer");
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var mib) ||
            mib is < MinimumMiB or > MaximumMiB)
            return new(null, "environment", "out_of_range");
        return new(mib * MiB, "environment");
    }
}
