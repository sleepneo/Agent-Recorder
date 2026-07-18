namespace AgentRecorder.Infrastructure;

/// <summary>
/// Read-only, thread-safe provider that returns a bounded statistical summary
/// of recent recording-performance traces. Implementations must never throw:
/// errors are represented inside <see cref="PerformanceSummary.Status"/> and
/// <see cref="PerformanceSummary.Quality.ReasonCode"/>.
/// </summary>
public interface IPerformanceSummaryProvider
{
    PerformanceSummary GetSummary();
}
