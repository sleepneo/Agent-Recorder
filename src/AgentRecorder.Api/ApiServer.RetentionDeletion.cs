using System.Text.Json;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    private const string DeletionPath="/retention/deletion-requests";
    private readonly RetentionHostKeepLatestProtection _retentionHostProtection;

    private object RetentionHostProtectionCapability(bool deletionSupported) => new
    {
        effective_count = _retentionHostProtection.EffectiveCount,
        source = _retentionHostProtection.Source,
        environment_variable = RetentionHostKeepLatestProtection.EnvironmentVariable,
        invalid_reason = _retentionHostProtection.InvalidReason,
        enforcement_supported = deletionSupported && _retentionDeletionGateway?.HostProtection is not null
    };
    private static bool IsDeletionPath(string path)=>path.StartsWith(Prefix+DeletionPath,StringComparison.Ordinal);
    private bool DeletionSupported=>_retentionDeletionGateway?.Supported==true&&!string.Equals(_tray.HostMode,"headless",StringComparison.OrdinalIgnoreCase)&&DeletionReady;
    private bool DeletionReady=>_acceptingStopped==0&&_stopped==0&&(_readiness is null||
        _readiness.ReadObservation() is {Disposed:false,PublishedReadyFile:not null,StartupInteractiveDesktopReady:true});
    private static ApiException InvalidDeletion()=>new(400,"INVALID_ARGUMENT","Deletion requests accept one recording_id and one Idempotency-Key; queries accept one request id only.",new{suggested_action="fix_request"});

    private static RetentionDeletionPolicy ParseDeletionPolicy(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException();
        var fields = value.EnumerateObject().ToArray();
        if (fields.Length != 2 || fields.Count(p => p.Name == "completed_before") != 1 ||
            fields.Count(p => p.Name == "keep_latest_count") != 1) throw new ArgumentException();

        var cutoff = value.GetProperty("completed_before");
        var count = value.GetProperty("keep_latest_count");
        if (cutoff.ValueKind != JsonValueKind.String || count.ValueKind != JsonValueKind.Number ||
            count.GetRawText().Any(c => c is < '0' or > '9') || !count.TryGetInt32(out var n) ||
            n is < 1 or > 1000) throw new ArgumentException();

        var before = ParseExplicitUtcTime(cutoff.GetString()!);
        if (before > DateTimeOffset.UtcNow) throw new ArgumentException();
        return new(before, n);
    }

    private string RouteDeletion(string method,string sub,HttpRequest request,string body,string requestId,CancellationToken cancellation,ref int status)
    {
        var create=method=="POST"&&sub==DeletionPath;
        var id=sub.StartsWith(DeletionPath+"/",StringComparison.Ordinal)?sub[(DeletionPath.Length+1)..]:null;
        if(request.Query.Count!=0||!create&&(method!="GET"||id is null||id.Length!=32||id.Any(c=>c is not(>= '0' and <= '9' or >= 'a' and <= 'f')))||!create&&body.Length!=0)throw InvalidDeletion();
        string? recording=null,key=null;RetentionDeletionPolicy? policy=null;
        if(create)
        {
            try
            {
                if(body.Length is 0 or >2048||request.HasDuplicateHeader("Idempotency-Key"))throw new ArgumentException();
                using var json=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=4,AllowTrailingCommas=false,CommentHandling=JsonCommentHandling.Disallow});
                if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new ArgumentException();var fields=json.RootElement.EnumerateObject().ToArray();
                if(fields.Length is <1 or >2 || fields.Any(p=>p.Name is not("recording_id" or "policy")) || fields.Count(p=>p.Name=="recording_id")!=1 || fields.Count(p=>p.Name=="policy")>1 || json.RootElement.GetProperty("recording_id").ValueKind!=JsonValueKind.String)throw new ArgumentException();
                if (json.RootElement.TryGetProperty("policy", out var value)) policy = ParseDeletionPolicy(value);
                recording=json.RootElement.GetProperty("recording_id").GetString();if(!RetentionDeletionCoordinator.ValidRecordingId(recording))throw new ArgumentException();
                key=RetentionDeletionCoordinator.NormalizeKey(request.Headers.GetValueOrDefault("Idempotency-Key"));
            }
            catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException){throw InvalidDeletion();}
        }
        if(!DeletionSupported)throw new ApiException(503,"RETENTION_DELETION_UNAVAILABLE","Local interactive deletion confirmation is unavailable or the host is not ready.",new{suggested_action="use_ready_interactive_app_and_query_same_request"});
        try
        {
            if(create)
            {
                var created=(policy is null?_retentionDeletionGateway!.RequestAsync(recording!,key!,cancellation):_retentionDeletionGateway!.PolicySupported?_retentionDeletionGateway.RequestWithPolicyAsync(recording!,key!,policy,cancellation):throw new DeletionRequestUnavailableException("policy_unsupported")).GetAwaiter().GetResult();status=202;return ApiResponse.Ok(created,requestId);
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
