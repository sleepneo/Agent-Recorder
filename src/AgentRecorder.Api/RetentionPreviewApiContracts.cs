using AgentRecorder.Core;
using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public interface IRetentionPreviewGateway
{
    RetentionPreviewPage GetPage(int limit, DateTimeOffset completedBefore, string? cursor);
}

public sealed class SqliteRetentionPreviewGateway(RetentionPreviewQueryService query,
    Func<(string Sid, string Session)> identity) : IRetentionPreviewGateway
{
    public RetentionPreviewPage GetPage(int limit, DateTimeOffset completedBefore, string? cursor)
    {
        try { var owner = identity(); return query.GetPage(owner.Sid, owner.Session, limit, completedBefore, cursor); }
        catch (Exception e) when (e is not UnifiedHistoryArgumentException and not RetentionPreviewUnavailableException)
        { throw new RetentionPreviewUnavailableException(e); }
    }
}
