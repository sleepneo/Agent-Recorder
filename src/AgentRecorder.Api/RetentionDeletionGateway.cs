using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public interface IRetentionDeletionGateway
{
    bool Supported {get;}
    string? HostShutdownBusyReason => "deletion_state_unknown";
    bool PolicySupported=>false;
    RetentionHostKeepLatestProtection? HostProtection => null;
    Task<RetentionDeletionRequestResult> RequestWithPolicyAsync(string recordingId,string key,RetentionDeletionPolicy policy,CancellationToken cancellation=default)=>Task.FromException<RetentionDeletionRequestResult>(new DeletionRequestUnavailableException("policy_unsupported"));
    Task<RetentionDeletionRequestResult> RequestAsync(string recordingId,string key,CancellationToken cancellation=default);
    Task<RetentionDeletionRequestResult?> ReadAsync(string requestId,CancellationToken cancellation=default);
    void Stop();
    Task<bool> WaitForIdleAsync();
}

public sealed class SqliteRetentionDeletionGateway(RetentionDeletionCoordinator coordinator):IRetentionDeletionGateway
{
    public IRetentionBatchDeletionGateway CreateBatchGateway()=>new SqliteRetentionBatchDeletionGateway(coordinator.CreateBatchCoordinator());
    public bool Supported=>coordinator.Supported;
    public bool PolicySupported=>Supported;
    public RetentionHostKeepLatestProtection HostProtection => coordinator.HostProtection;
    public Task<RetentionDeletionRequestResult> RequestWithPolicyAsync(string recordingId,string key,RetentionDeletionPolicy policy,CancellationToken cancellation=default)=>coordinator.RequestAsync(recordingId,key,cancellation,policy);
    public string? HostShutdownBusyReason => coordinator.HostShutdownBusyReason;
    public Task<RetentionDeletionRequestResult> RequestAsync(string recordingId,string key,CancellationToken cancellation=default)=>coordinator.RequestAsync(recordingId,key,cancellation);
    public Task<RetentionDeletionRequestResult?> ReadAsync(string id,CancellationToken cancellation=default)=>coordinator.ReadAsync(id,cancellation);
    public void Stop()=>coordinator.Dispose();
    public Task<bool> WaitForIdleAsync()=>coordinator.WaitForIdleAsync();
}
