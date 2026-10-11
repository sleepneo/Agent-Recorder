using AgentRecorder.Core;

namespace AgentRecorder.Api;

public interface IUnifiedHistoryGateway
{
    IReadOnlyList<string> SupportedSources { get; }
    UnifiedHistoryPage GetPage(int limit, UnifiedHistoryFilters filters, string? cursor);
}
