using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public interface IHistoryArtifactCheckGateway
{
    IReadOnlyList<string> SupportedSources {get;}
    Task<HistoryArtifactCheckResult?> ReadAsync(string entryId,CancellationToken cancellation=default);
}

public sealed class SqliteHistoryArtifactCheckGateway(HistoryArtifactCheckService service,Func<(string Sid,string Session)> identity)
    :IHistoryArtifactCheckGateway
{
    public IReadOnlyList<string> SupportedSources => new[]{"plan","future_window"};
    public Task<HistoryArtifactCheckResult?> ReadAsync(string entryId,CancellationToken cancellation=default)
    {
        var (sid,session)=identity();return service.ReadAsync(entryId,sid,session,cancellation);
    }
}
