using AgentRecorder.Persistence;
namespace AgentRecorder.Api;

public interface IRetentionBatchDeletionGateway
{
    bool Supported {get;}
    string? HostShutdownBusyReason {get;}
    Task<RetentionBatchDeletionRequestResult> RequestAsync(IReadOnlyList<string> ids,string key,CancellationToken cancellation=default);
    Task<RetentionBatchDeletionRequestResult?> ReadAsync(string id,CancellationToken cancellation=default);
    void Stop();
    Task<bool> WaitForIdleAsync();
}
public sealed class SqliteRetentionBatchDeletionGateway(RetentionBatchDeletionCoordinator coordinator):IRetentionBatchDeletionGateway
{
    public bool Supported=>coordinator.Supported;
    public string? HostShutdownBusyReason=>coordinator.HostShutdownBusyReason;
    public Task<RetentionBatchDeletionRequestResult> RequestAsync(IReadOnlyList<string> ids,string key,CancellationToken cancellation=default)=>coordinator.RequestAsync(ids,key,cancellation);
    public Task<RetentionBatchDeletionRequestResult?> ReadAsync(string id,CancellationToken cancellation=default)=>coordinator.ReadAsync(id,cancellation);
    public void Stop()=>coordinator.Dispose();
    public Task<bool> WaitForIdleAsync()=>coordinator.WaitForIdleAsync();
}
