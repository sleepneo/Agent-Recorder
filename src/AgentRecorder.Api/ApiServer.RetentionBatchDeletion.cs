using System.Text;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    private const string BatchDeletionPath="/retention/batch-deletion-requests";
    private const int BatchDeletionBodyLimit=4096;
    private static bool IsBatchDeletionPath(string path)=>path.StartsWith(Prefix+BatchDeletionPath,StringComparison.Ordinal);
    private bool BatchDeletionSupported=>_retentionBatchDeletionGateway?.Supported==true&&_tray.HostMode!="headless"&&DeletionReady;
    private sealed class BatchDeletionRequestException():ApiException(400,"INVALID_ARGUMENT","Use 1-5 distinct recording_ids, one Idempotency-Key and an optional policy. Queries accept a batch ID only.");
    private static ApiException InvalidBatchDeletion()=>new BatchDeletionRequestException();
    private string RouteBatchDeletion(string method,string sub,HttpRequest request,string body,string requestId,CancellationToken cancellation,ref int status)
    {
        var create=method=="POST"&&sub==BatchDeletionPath;
        var id=sub.StartsWith(BatchDeletionPath+"/",StringComparison.Ordinal)?sub[(BatchDeletionPath.Length+1)..]:null;
        if(request.Query.Count!=0||!create&&(method!="GET"||id is null||id.Length!=32||id.Any(c=>c is not(>= '0' and <= '9' or >= 'a' and <= 'f')))||!create&&body.Length!=0)throw InvalidBatchDeletion();
        string[]? ids=null;string? key=null;
        RetentionDeletionPolicy? policy = null;
        if(create)
        {
            try
            {
                if(Encoding.UTF8.GetByteCount(body) is 0 or >BatchDeletionBodyLimit||request.HasDuplicateHeader("Idempotency-Key"))throw new ArgumentException();
                using var json=JsonDocument.Parse(body,new JsonDocumentOptions {MaxDepth=4});
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException();
                var fields = json.RootElement.EnumerateObject().ToArray();
                if (fields.Length is < 1 or > 2 || fields.Any(p => p.Name is not ("recording_ids" or "policy")) ||
                    fields.Count(p => p.Name == "recording_ids") != 1 || fields.Count(p => p.Name == "policy") > 1)
                    throw new ArgumentException();
                var recordings = json.RootElement.GetProperty("recording_ids");
                if (recordings.ValueKind != JsonValueKind.Array || recordings.GetArrayLength() is < 1 or > 5 ||
                    recordings.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String)) throw new ArgumentException();
                ids = recordings.EnumerateArray().Select(i => i.GetString()!).ToArray();
                RetentionBatchDeletionCoordinator.ValidateIds(ids);
                if (json.RootElement.TryGetProperty("policy", out var value)) policy = ParseDeletionPolicy(value);
                key=RetentionDeletionCoordinator.NormalizeKey(request.Headers.GetValueOrDefault("Idempotency-Key"));
            }
            catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException){throw InvalidBatchDeletion();}
        }
        if(!BatchDeletionSupported)throw new ApiException(503,"RETENTION_BATCH_DELETION_UNAVAILABLE","A ready interactive App with per-item local confirmation is required.");
        try
        {
            if (create)
            {
                var gateway = _retentionBatchDeletionGateway!;
                var work = policy is null
                    ? gateway.RequestAsync(ids!, key!, cancellation)
                    : gateway.PolicySupported
                        ? gateway.RequestWithPolicyAsync(ids!, key!, policy, cancellation)
                        : throw new DeletionRequestUnavailableException("policy_unsupported");
                var result = work.GetAwaiter().GetResult();
                status = 202;
                return ApiResponse.Ok(result, requestId);
            }
            var row=_retentionBatchDeletionGateway!.ReadAsync(id!,cancellation).GetAwaiter().GetResult();
            if(row is null)throw new ApiException(404,"BATCH_DELETION_REQUEST_NOT_FOUND","No batch exists for this host identity.");return ApiResponse.Ok(row,requestId);
        }
        catch(ApiException){throw;}
        catch(DeletionRequestConflictException){throw new ApiException(409,"IDEMPOTENCY_KEY_CONFLICT","Replay the same ordered body and key to find the original batch.");}
        catch(DeletionRequestUnavailableException e){throw new ApiException(503,"RETENTION_BATCH_DELETION_UNAVAILABLE","Batch state is temporarily unavailable.",new{reason_code=e.Reason,suggested_action="query_same_batch_do_not_resume_or_retry_unknown"});}
        catch{throw new ApiException(503,"RETENTION_BATCH_DELETION_UNAVAILABLE","Batch state is unavailable; do not replay execution.");}
    }
}
