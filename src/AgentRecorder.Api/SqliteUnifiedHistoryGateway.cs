using AgentRecorder.Persistence;
using AgentRecorder.Core;

namespace AgentRecorder.Api;

/// <summary>Identity is supplied by the host, never by HTTP request parameters.</summary>
public sealed class SqliteUnifiedHistoryGateway : IUnifiedHistoryGateway
{
    private readonly UnifiedHistoryQueryService _query;
    private readonly Func<(string Sid, string Session)> _identity;
    public SqliteUnifiedHistoryGateway(UnifiedHistoryQueryService query, Func<(string Sid, string Session)> identity)
    { _query = query; _identity = identity; }
    public IReadOnlyList<string> SupportedSources => _query.SupportedSources;
    public UnifiedHistoryPage GetPage(int limit, UnifiedHistoryFilters filters, string? cursor)
    {
        try
        { var identity = _identity(); return _query.GetPage(identity.Sid, identity.Session, limit, filters, cursor); }
        catch (Exception ex) when (ex is not UnifiedHistoryArgumentException and not UnifiedHistoryUnavailableException)
        { throw new UnifiedHistoryUnavailableException(ex); }
    }
}
