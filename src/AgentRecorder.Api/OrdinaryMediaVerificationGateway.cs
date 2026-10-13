using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public interface IOrdinaryMediaVerificationGateway
{
    Task<OrdinaryMediaVerificationResult?> VerifyAsync(string recordingId,CancellationToken cancellation);
}

public sealed class SqliteOrdinaryMediaVerificationGateway(OrdinaryMediaVerificationService service) : IOrdinaryMediaVerificationGateway
{
    public Task<OrdinaryMediaVerificationResult?> VerifyAsync(string recordingId,CancellationToken cancellation)=>service.VerifyAsync(recordingId,cancellation);
}
