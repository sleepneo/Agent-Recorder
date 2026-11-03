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
    private sealed class BatchDeletionRequestException():ApiException(400,"INVALID_ARGUMENT","Use 1-5 distinct recording_ids and one Idempotency-Key; no extra fields, query or GET body.");
    private static ApiException InvalidBatchDeletion()=>new BatchDeletionRequestException();
    private string RouteBatchDeletion(string method,string sub,HttpRequest request,string body,string requestId,CancellationToken cancellation,ref int status)
    {
        var create=method=="POST"&&sub==BatchDeletionPath;
        var id=sub.StartsWith(BatchDeletionPath+"/",StringComparison.Ordinal)?sub[(BatchDeletionPath.Length+1)..]:null;
        if(request.Query.Count!=0||!create&&(method!="GET"||id is null||id.Length!=32||id.Any(c=>c is not(>= '0' and <= '9' or >= 'a' and <= 'f')))||!create&&body.Length!=0)throw InvalidBatchDeletion();
        string[]? ids=null;string? key=null;
        if(create)
        {
            try
            {
                if(Encoding.UTF8.GetByteCount(body) is 0 or >BatchDeletionBodyLimit||request.HasDuplicateHeader("Idempotency-Key"))throw new ArgumentException();
                using var json=JsonDocument.Parse(body,new JsonDocumentOptions {MaxDepth=4});
                if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException();var fields=json.RootElement.EnumerateObject().ToArray();
                if(fields.Length!=1||fields[0].Name!="recording_ids"||fields[0].Value.ValueKind!=JsonValueKind.Array||fields[0].Value.GetArrayLength() is <1 or >5||fields[0].Value.EnumerateArray().Any(i=>i.ValueKind!=JsonValueKind.String))throw new ArgumentException();
                ids=fields[0].Value.EnumerateArray().Select(i=>i.GetString()!).ToArray();RetentionBatchDeletionCoordinator.ValidateIds(ids);
                key=RetentionDeletionCoordinator.NormalizeKey(request.Headers.GetValueOrDefault("Idempotency-Key"));
            }
            catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException){throw InvalidBatchDeletion();}
        }
        if(!BatchDeletionSupported)throw new ApiException(503,"RETENTION_BATCH_DELETION_UNAVAILABLE","A ready interactive App with per-item local confirmation is required.");
        try
        {
            if(create){var result=_retentionBatchDeletionGateway!.RequestAsync(ids!,key!,cancellation).GetAwaiter().GetResult();status=202;return ApiResponse.Ok(result,requestId);}
            var row=_retentionBatchDeletionGateway!.ReadAsync(id!,cancellation).GetAwaiter().GetResult();
            if(row is null)throw new ApiException(404,"BATCH_DELETION_REQUEST_NOT_FOUND","No batch exists for this host identity.");return ApiResponse.Ok(row,requestId);
        }
        catch(ApiException){throw;}
        catch(DeletionRequestConflictException){throw new ApiException(409,"IDEMPOTENCY_KEY_CONFLICT","Replay the same ordered body and key to find the original batch.");}
        catch(DeletionRequestUnavailableException e){throw new ApiException(503,"RETENTION_BATCH_DELETION_UNAVAILABLE","Batch state is temporarily unavailable.",new{reason_code=e.Reason,suggested_action="query_same_batch_do_not_resume_or_retry_unknown"});}
        catch{throw new ApiException(503,"RETENTION_BATCH_DELETION_UNAVAILABLE","Batch state is unavailable; do not replay execution.");}
    }
}
