using System;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Default no-op / no-data provider used when a production summary provider is
/// not injected. Keeps <c>/api/v1/capabilities</c> stable and avoids accidental
/// reliance on unconfigured diagnostics.
/// </summary>
public sealed class NoDataPerformanceSummaryProvider : IPerformanceSummaryProvider
{
    public static readonly NoDataPerformanceSummaryProvider Instance = new();

    private NoDataPerformanceSummaryProvider()
    {
    }

    public PerformanceSummary GetSummary() =>
        PerformanceSummary.NoData(DateTime.UtcNow, RollingJsonlPerformanceSummaryProviderConstants.DefaultMaxTracesPerGroup);
}

/// <summary>
/// Shared constants so the no-data provider and tests can reference the same
/// default window size without creating a hard dependency on the production
/// rolling-JSONL implementation.
/// </summary>
public static class RollingJsonlPerformanceSummaryProviderConstants
{
    public const int DefaultMaxTracesPerGroup = 50;
}
