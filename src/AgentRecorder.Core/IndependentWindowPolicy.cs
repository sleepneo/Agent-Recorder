using System.Globalization;

namespace AgentRecorder.Core;

// One immutable startup value shared by admission and capability projections.
public sealed record IndependentWindowPolicy
{
    public const string EnvironmentVariable = "AGENT_RECORDER_MAX_INDEPENDENT_WINDOWS";
    public const int DefaultLimit = 2;
    public const int HardLimit = 10;
    public int? ConfiguredLimit { get; }
    public string Source { get; }
    public bool Valid => ConfiguredLimit.HasValue;
    private IndependentWindowPolicy(int? limit, string source) { ConfiguredLimit = limit; Source = source; }
    internal static IndependentWindowPolicy ReadEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (value is null) return new(DefaultLimit, "default");
        return new(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= HardLimit ? n : null,
            "environment");
    }
}

public sealed record IndependentWindowBudgetSnapshot(int Reserved, int FinalizingOrUnknown, int? Remaining);
