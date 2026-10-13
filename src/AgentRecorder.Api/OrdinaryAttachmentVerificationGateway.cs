using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public interface IOrdinaryAttachmentVerificationGateway
{
    Task<OrdinaryAttachmentVerificationResult?> VerifyAsync(string recordingId, CancellationToken cancellation);
}

public sealed class SqliteOrdinaryAttachmentVerificationGateway(OrdinaryAttachmentVerificationService service)
    : IOrdinaryAttachmentVerificationGateway
{
    public Task<OrdinaryAttachmentVerificationResult?> VerifyAsync(string recordingId, CancellationToken cancellation)
        => service.VerifyAsync(recordingId, cancellation);
}
