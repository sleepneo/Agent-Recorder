using System.Globalization;

namespace AgentRecorder.Core;

// One immutable startup value shared by admission and capability projections.
public sealed record IndependentWindowPolicy
{
    public const string EnvironmentVariable = "AGENT_RECORDER_MAX_INDEPENDENT_WINDOWS";
    public const int DefaultLimit = 2;
    public const int HardLimit = 10;
    public const string PixelRateEnvironmentVariable = "AGENT_RECORDER_MAX_INDEPENDENT_WINDOW_PIXELS_PER_SECOND";
    // Arithmetic admission policy, not measured hardware capacity.
    public const long DefaultPixelsPerSecond = 497664000;
    public int? ConfiguredLimit { get; }
    public string Source { get; }
    public long? ConfiguredPixelsPerSecond { get; }
    public string PixelRateSource { get; }
    public bool Valid => ConfiguredLimit.HasValue && ConfiguredPixelsPerSecond.HasValue;
    public string? InvalidEnvironmentVariable => !ConfiguredLimit.HasValue ? EnvironmentVariable :
        !ConfiguredPixelsPerSecond.HasValue ? PixelRateEnvironmentVariable : null;
    private IndependentWindowPolicy(int? limit, string source, long? pixels, string pixelSource)
    { ConfiguredLimit = limit; Source = source; ConfiguredPixelsPerSecond = pixels; PixelRateSource = pixelSource; }
    internal static IndependentWindowPolicy ReadEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var pixels = Environment.GetEnvironmentVariable(PixelRateEnvironmentVariable);
        return new(value is null ? DefaultLimit :
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= HardLimit ? n : null,
            value is null ? "default" : "environment",
            pixels is null ? DefaultPixelsPerSecond :
                pixels.Length > 0 && pixels.All(c => c is >= '0' and <= '9') &&
                long.TryParse(pixels, NumberStyles.None, CultureInfo.InvariantCulture, out var rate) && rate > 0 ? rate : null,
            pixels is null ? "default" : "environment");
    }
}

public sealed record IndependentWindowBudgetSnapshot(int Reserved, int FinalizingOrUnknown, int? Remaining,
    long ReservedPixelsPerSecond, long FinalizingOrUnknownPixelsPerSecond, long? RemainingPixelsPerSecond);
