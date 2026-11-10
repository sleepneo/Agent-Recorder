using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public interface IRetentionEvaluationGateway
{
    bool SupportsKeepLatest => false;
    Task<RetentionEvaluationResult?> ReadWithKeepLatestAsync(IReadOnlyList<string> ids,DateTimeOffset before,int count,CancellationToken cancellation)
        =>Task.FromResult<RetentionEvaluationResult?>(null);
    Task<RetentionEvaluationResult?> ReadAsync(IReadOnlyList<string> ids,DateTimeOffset before,CancellationToken cancellation);
}
public sealed class SqliteRetentionEvaluationGateway(RetentionEvaluationService service,
    Func<(string Sid,string Session)> identity) : IRetentionEvaluationGateway
{
    public bool SupportsKeepLatest => true;
    public Task<RetentionEvaluationResult?> ReadWithKeepLatestAsync(IReadOnlyList<string> ids,DateTimeOffset before,int count,CancellationToken cancellation)
        =>service.ReadAsync(ids,before,identity,cancellation,count);
    public Task<RetentionEvaluationResult?> ReadAsync(IReadOnlyList<string> ids,DateTimeOffset before,CancellationToken cancellation)
        =>service.ReadAsync(ids,before,identity,cancellation);
}
