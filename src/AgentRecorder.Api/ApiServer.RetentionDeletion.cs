using System.Text.Json;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    private const string DeletionPath="/retention/deletion-requests";
    private static bool IsDeletionPath(string path)=>path.StartsWith(Prefix+DeletionPath,StringComparison.Ordinal);
    private bool DeletionSupported=>_retentionDeletionGateway?.Supported==true&&!string.Equals(_tray.HostMode,"headless",StringComparison.OrdinalIgnoreCase)&&DeletionReady;
    private bool DeletionReady=>_acceptingStopped==0&&_stopped==0&&(_readiness is null||
        _readiness.ReadObservation() is {Disposed:false,PublishedReadyFile:not null,StartupInteractiveDesktopReady:true});
    private static ApiException InvalidDeletion()=>new(400,"INVALID_ARGUMENT","Deletion requests accept one recording_id and one Idempotency-Key; queries accept one request id only.",new{suggested_action="fix_request"});
    private string RouteDeletion(string method,string sub,HttpRequest request,string body,string requestId,CancellationToken cancellation,ref int status)
    {
        var create=method=="POST"&&sub==DeletionPath;
        var id=sub.StartsWith(DeletionPath+"/",StringComparison.Ordinal)?sub[(DeletionPath.Length+1)..]:null;
        if(request.Query.Count!=0||!create&&(method!="GET"||id is null||id.Length!=32||id.Any(c=>c is not(>= '0' and <= '9' or >= 'a' and <= 'f')))||!create&&body.Length!=0)throw InvalidDeletion();
        string? recording=null,key=null;
        if(create)
        {
            try
            {
                if(body.Length is 0 or >2048||request.HasDuplicateHeader("Idempotency-Key"))throw new ArgumentException();
                using var json=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=4,AllowTrailingCommas=false,CommentHandling=JsonCommentHandling.Disallow});
                if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException();var fields=json.RootElement.EnumerateObject().ToArray();
                if(fields.Length!=1||fields[0].Name!="recording_id"||fields[0].Value.ValueKind!=JsonValueKind.String)throw new ArgumentException();
                recording=fields[0].Value.GetString();if(!RetentionDeletionCoordinator.ValidRecordingId(recording))throw new ArgumentException();
                key=RetentionDeletionCoordinator.NormalizeKey(request.Headers.GetValueOrDefault("Idempotency-Key"));
            }
            catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException){throw InvalidDeletion();}
        }
        if(!DeletionSupported)throw new ApiException(503,"RETENTION_DELETION_UNAVAILABLE","Local interactive deletion confirmation is unavailable or the host is not ready.",new{suggested_action="use_ready_interactive_app_and_query_same_request"});
        try
        {
            if(create)
            {
                var created=_retentionDeletionGateway!.RequestAsync(recording!,key!,cancellation).GetAwaiter().GetResult();status=202;return ApiResponse.Ok(created,requestId);
            }
            var result=_retentionDeletionGateway!.ReadAsync(id!,cancellation).GetAwaiter().GetResult();
            if(result is null)throw new ApiException(404,"DELETION_REQUEST_NOT_FOUND","No deletion request exists for this host identity.",new{suggested_action="check_request_id_and_host"});
            return ApiResponse.Ok(result,requestId);
        }
        catch(ApiException){throw;}
        catch(DeletionRequestConflictException){throw new ApiException(409,"IDEMPOTENCY_KEY_CONFLICT","The key is already bound to a different deletion request.",new{suggested_action="replay_original_body_and_key_to_find_original_request"});}
        catch(DeletionRequestUnavailableException e){throw new ApiException(503,"RETENTION_DELETION_UNAVAILABLE","Deletion request state is temporarily unavailable.",new{reason_code=e.Reason,suggested_action="query_same_request_do_not_retry_execution"});}
        catch{throw new ApiException(503,"RETENTION_DELETION_UNAVAILABLE","Deletion request state is unavailable.",new{suggested_action="query_same_request_do_not_retry_execution"});}
    }
}
