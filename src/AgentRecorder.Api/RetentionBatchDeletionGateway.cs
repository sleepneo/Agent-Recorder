using AgentRecorder.Persistence;
namespace AgentRecorder.Api;

public interface IRetentionBatchDeletionGateway
{
    bool Supported {get;}
    bool PolicySupported => false;
    string? HostShutdownBusyReason {get;}
    Task<RetentionBatchDeletionRequestResult> RequestAsync(IReadOnlyList<string> ids,string key,CancellationToken cancellation=default);
    Task<RetentionBatchDeletionRequestResult> RequestWithPolicyAsync(IReadOnlyList<string> ids, string key,
        RetentionDeletionPolicy policy, CancellationToken cancellation = default)
        => Task.FromException<RetentionBatchDeletionRequestResult>(new DeletionRequestUnavailableException("policy_unsupported"));
    Task<RetentionBatchDeletionRequestResult?> ReadAsync(string id,CancellationToken cancellation=default);
    void Stop();
    Task<bool> WaitForIdleAsync();
}
public sealed class SqliteRetentionBatchDeletionGateway(RetentionBatchDeletionCoordinator coordinator):IRetentionBatchDeletionGateway
{
    public bool Supported=>coordinator.Supported;
    public bool PolicySupported => Supported;
    public string? HostShutdownBusyReason=>coordinator.HostShutdownBusyReason;
    public Task<RetentionBatchDeletionRequestResult> RequestAsync(IReadOnlyList<string> ids,string key,CancellationToken cancellation=default)=>coordinator.RequestAsync(ids,key,cancellation);
    public Task<RetentionBatchDeletionRequestResult> RequestWithPolicyAsync(IReadOnlyList<string> ids, string key,
        RetentionDeletionPolicy policy, CancellationToken cancellation = default)
        => coordinator.RequestAsync(ids, key, cancellation, policy);
    public Task<RetentionBatchDeletionRequestResult?> ReadAsync(string id,CancellationToken cancellation=default)=>coordinator.ReadAsync(id,cancellation);
    public void Stop()=>coordinator.Dispose();
    public Task<bool> WaitForIdleAsync()=>coordinator.WaitForIdleAsync();
}
