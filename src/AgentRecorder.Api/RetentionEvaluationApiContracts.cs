using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public interface IRetentionEvaluationGateway
{
    Task<RetentionEvaluationResult?> ReadAsync(IReadOnlyList<string> ids,DateTimeOffset before,CancellationToken cancellation);
}
public sealed class SqliteRetentionEvaluationGateway(RetentionEvaluationService service,
    Func<(string Sid,string Session)> identity) : IRetentionEvaluationGateway
{
    public Task<RetentionEvaluationResult?> ReadAsync(IReadOnlyList<string> ids,DateTimeOffset before,CancellationToken cancellation)
        =>service.ReadAsync(ids,before,identity,cancellation);
}
