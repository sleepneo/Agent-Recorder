using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.Api;

public sealed partial class ApiServer
{
    private const string EvaluationPath="/retention/evaluations";
    private const int EvaluationBodyLimit=16*1024;
    private static bool IsRetentionEvaluationPath(string path)=>path.Split('?')[0]==Prefix+EvaluationPath;
    private sealed class EvaluationRequestException() : ApiException(400,"INVALID_ARGUMENT",
        "Use recording_ids (1-20 distinct IDs) and an explicit UTC completed_before only; no query parameters.");
    private static ApiException InvalidEvaluation()=>new EvaluationRequestException();
    // Send FIN after the error, then discard only a bounded amount of unread
    // transport data. Closing with unread bytes can reset away the JSON reply.
    private static async Task FinishRejectedEvaluation(TcpClient client,Stream stream)
    {
        try
        {
            client.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
            using var deadline=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var buffer=new byte[4096];var remaining=EvaluationBodyLimit;
            while(remaining>0)
            {
                var read=await stream.ReadAsync(buffer.AsMemory(0,Math.Min(buffer.Length,remaining)),deadline.Token);
                if(read==0)break;remaining-=read;
            }
        }
        catch(Exception e) when(e is IOException or OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
    }
    private string PostRetentionEvaluation(HttpRequest request,string body,string requestId,CancellationToken cancellation)
    {
        string[] ids;DateTimeOffset before;
        try
        {
            if(request.Query.Count!=0||Encoding.UTF8.GetByteCount(body)>EvaluationBodyLimit)throw InvalidEvaluation();
            using var json=JsonDocument.Parse(body,new JsonDocumentOptions {MaxDepth=4});
            if(json.RootElement.ValueKind!=JsonValueKind.Object)throw InvalidEvaluation();
            var fields=json.RootElement.EnumerateObject().ToArray();
            if(fields.Length!=2||fields.Count(p=>p.Name=="recording_ids")!=1||fields.Count(p=>p.Name=="completed_before")!=1)throw InvalidEvaluation();
            var array=json.RootElement.GetProperty("recording_ids");var cutoff=json.RootElement.GetProperty("completed_before");
            if(array.ValueKind!=JsonValueKind.Array||array.GetArrayLength() is <1 or >20||
                array.EnumerateArray().Any(x=>x.ValueKind!=JsonValueKind.String)||cutoff.ValueKind!=JsonValueKind.String)throw InvalidEvaluation();
            ids=array.EnumerateArray().Select(x=>x.GetString()!).ToArray();before=ParseExplicitUtcTime(cutoff.GetString()!);
            RetentionEvaluationService.ValidateRequest(ids,before,DateTimeOffset.UtcNow);
        }
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException){throw InvalidEvaluation();}
        if(_retentionEvaluationGateway is null || _acceptingStopped!=0 || _stopped!=0 ||
            _readiness is not null && (_readiness.ReadObservation() is not {Disposed:false,PublishedReadyFile:not null} ready ||
                _tray.HostMode!="headless"&&ready.StartupInteractiveDesktopReady!=true))
            throw new ApiException(503,"RETENTION_EVALUATION_UNAVAILABLE","Read-only retention evaluation is not ready.");
        try
        {
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            RetentionEvaluationResult? result;
            try {result=_retentionEvaluationGateway.ReadAsync(ids,before,budget.Token)
                .WaitAsync(TimeSpan.FromMilliseconds(RetentionEvaluationService.DeadlineMs),budget.Token).GetAwaiter().GetResult();}
            finally {budget.Cancel();}
            if(result is not {State:"complete",Page:{} page} || page.Items.Count!=ids.Length ||
                page.EvaluatedCount!=ids.Length || page.PageSize!=ids.Length || page.CompletedBefore!=before || page.NextCursor is not null ||
                page.Items.Where((item,index)=>item.RecordingId!=ids[index]).Any())
                throw new ApiException(503,"RETENTION_EVALUATION_UNAVAILABLE","Read-only retention evaluation is unavailable.",
                    new {reason_code=result?.State is "busy" or "timeout" or "cancelled" ? result.State : "failed"});
            return ApiResponse.Ok(new
            {
                read_only=true,deletion_supported=false,reference_protection_ready=false,metadata_only=true,bundle_included=false,
                evaluation_started_at_utc=result.StartedAtUtc,evaluation_completed_at_utc=result.CompletedAtUtc,
                completed_before=before.ToString("O",CultureInfo.InvariantCulture),
                items=page.Items.Select(e=>new {recording_id=e.RecordingId,status=e.Status,completed_at_utc=e.CompletedAtUtc,
                    evaluation=e.Evaluation,reason_code=e.ReasonCode,age_candidate=e.AgeCandidate,
                    recorded_path=e.RecordedPath,recorded_bytes=e.RecordedBytes,observed_file_bytes=e.ObservedFileBytes}),
                evaluated_count=page.EvaluatedCount,age_candidate_count=page.AgeCandidateCount,
                observed_candidate_bytes=page.ObservedCandidateBytes,observed_candidate_file_count=page.ObservedCandidateFileCount,
                unknown_candidate_file_count=page.UnknownCandidateFileCount,
                unknown_file_count=page.Items.Count(e=>e.ObservedFileBytes is null),
                observed_bytes_complete=page.Items.All(e=>e.ObservedFileBytes is not null),
                bytes_scope="requested_age_candidates_mp4_logical_length_not_reclaimable_space",
                observation_semantics="database_snapshot_then_runtime_and_sequential_file_samples_not_atomic"
            },requestId);
        }
        catch(ApiException){throw;}
        catch(TimeoutException){throw new ApiException(503,"RETENTION_EVALUATION_UNAVAILABLE","Read-only retention evaluation timed out.",new {reason_code="timeout"});}
        catch(OperationCanceledException){throw new ApiException(503,"RETENTION_EVALUATION_UNAVAILABLE","Read-only retention evaluation was cancelled.",new {reason_code="cancelled"});}
        catch {throw new ApiException(503,"RETENTION_EVALUATION_UNAVAILABLE","Read-only retention evaluation is unavailable.");}
    }
}
