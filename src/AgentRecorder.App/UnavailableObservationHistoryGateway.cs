using AgentRecorder.Api;
using AgentRecorder.Core;

namespace AgentRecorder.App;

internal sealed class UnavailableObservationHistoryGateway : IUnifiedHistoryGateway
{
    internal static readonly IUnifiedHistoryGateway Instance = new UnavailableObservationHistoryGateway();
    public IReadOnlyList<string> SupportedSources => Array.Empty<string>();
    public UnifiedHistoryPage GetPage(int limit, UnifiedHistoryFilters filters, string? cursor) => throw new UnifiedHistoryUnavailableException();
}
