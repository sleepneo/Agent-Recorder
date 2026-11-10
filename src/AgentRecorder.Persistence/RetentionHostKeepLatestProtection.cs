using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

public sealed class RetentionHostKeepLatestProtection
{
    public const string EnvironmentVariable = "AGENT_RECORDER_RETENTION_PROTECT_LATEST_COUNT";
    internal const string ProtectedReason = "within_host_keep_latest";
    internal const string InvalidConfigurationReason = "host_keep_latest_configuration_invalid";
    internal const string UnavailableReason = "host_keep_latest_unavailable";

    private static readonly Lazy<RetentionHostKeepLatestProtection> ProcessSetting =
        new(() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable)));

    public static RetentionHostKeepLatestProtection Startup => ProcessSetting.Value;
    public int? EffectiveCount { get; }
    public string Source { get; }
    public string? InvalidReason { get; }

    private RetentionHostKeepLatestProtection(int? count, string source, string? invalidReason = null)
    { EffectiveCount = count; Source = source; InvalidReason = invalidReason; }

    internal static RetentionHostKeepLatestProtection Parse(string? value)
    {
        if (value is null) return new(0, "default");
        if (value.Length == 0 || !value.All(char.IsAsciiDigit)) return new(null, "environment", "invalid_integer");
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > 1000)
            return new(null, "environment", "out_of_range");
        return new(count, "environment");
    }

    internal static bool IsHostReason(string? reason) => reason is ProtectedReason or InvalidConfigurationReason or UnavailableReason;

    internal string? Check(RetentionDeletionPolicy? policy, UnifiedHistoryQueryService history,
        SqliteConnection connection, SqliteTransaction transaction, string id, DateTimeOffset completed, CancellationToken cancellation)
    {
        if (EffectiveCount is not int hostCount) return InvalidConfigurationReason;
        var count = Math.Max(hostCount, policy?.KeepLatestCount ?? 0);
        if (count == 0) return null;
        int rank;
        try { rank = history.ReadKeepLatestRank(connection, transaction, count, id, cancellation); }
        catch (Exception error) when (hostCount > 0 && error is SqliteException or InvalidDataException or TimeoutException)
        { return UnavailableReason; }
        if (rank <= hostCount) return ProtectedReason;
        if (policy is null) return null;
        if (rank <= policy.KeepLatestCount) return "within_keep_latest";
        return completed >= policy.CompletedBefore ? "completion_not_before_cutoff" : null;
    }
}
