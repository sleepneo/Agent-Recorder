using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public interface IOrdinaryArtifactCheckGateway
{
    Task<OrdinaryArtifactCheckResult?> ReadAsync(string recordingId,CancellationToken cancellation);
}

public sealed class SqliteOrdinaryArtifactCheckGateway : IOrdinaryArtifactCheckGateway
{
    private readonly OrdinaryArtifactCheckService _service;
    public SqliteOrdinaryArtifactCheckGateway(OrdinaryArtifactCheckService service)=>_service=service;
    public Task<OrdinaryArtifactCheckResult?> ReadAsync(string recordingId,CancellationToken cancellation)=>_service.ReadAsync(recordingId,cancellation);
}
