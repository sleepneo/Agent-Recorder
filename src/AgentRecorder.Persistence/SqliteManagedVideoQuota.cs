using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>Composes the startup quota with the existing registered-usage reader.</summary>
public static class SqliteManagedVideoQuota
{
    public static void Configure(RecordingEngine engine, SqliteOperationalStore store, string trustedDataDirectory)
    {
        var setting = ManagedVideoQuotaSetting.Parse(Environment.GetEnvironmentVariable(ManagedVideoQuotaSetting.EnvironmentVariable));
        var usage = new ManagedVideoUsage(store, trustedDataDirectory);
        engine.ConfigureManagedVideoQuota(new(setting, trustedDataDirectory, async () =>
        {
            var sample = await usage.ReadAsync().ConfigureAwait(false);
            if (sample.UnavailableReason is { } reason) return new(null, reason);
            return sample.ReasonCodes.Length == 0
                ? new(sample.RecordedLogicalBytes)
                : new(null, "recorded_usage_incomplete");
        }));
    }
}
