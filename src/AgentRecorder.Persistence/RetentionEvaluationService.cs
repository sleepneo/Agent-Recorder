using AgentRecorder.Core;

namespace AgentRecorder.Persistence;

public sealed record RetentionEvaluationResult(string State, DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc, RetentionPreviewPage? Page);

// Shares the process-wide actual metadata worker, including work surviving HTTP cancellation.
public sealed class RetentionEvaluationService
{
    public const int MaxIds = 20;
    public const int MaxIdLength = 200;
    public const int DeadlineMs = 3000;
    private readonly Func<string,string,IReadOnlyList<string>,CancellationToken,IReadOnlyDictionary<string,OrdinaryRetentionEvidence?>> _read;
    private readonly RetentionPreviewQueryService _kernel;
    private readonly Func<DateTimeOffset> _now;
    internal Func<CancellationToken,Task> DeadlineForTests { get; set; } = token=>Task.Delay(DeadlineMs,token);
    internal Action? WorkerExitedForTests { get; set; }
    public RetentionEvaluationService(UnifiedHistoryQueryService history,string dataDirectory,
        IOrdinaryRuntimeOutputUseProvider? runtime)
        :this(history.GetOrdinaryEvidenceByIds,new RetentionPreviewQueryService(history,dataDirectory,runtime)) { }
    internal RetentionEvaluationService(
        Func<string,string,IReadOnlyList<string>,CancellationToken,IReadOnlyDictionary<string,OrdinaryRetentionEvidence?>> read,
        RetentionPreviewQueryService kernel,Func<DateTimeOffset>? now=null)
    { _read=read;_kernel=kernel;_now=now??(()=>DateTimeOffset.UtcNow); }

    public static void ValidateRequest(IReadOnlyList<string> ids,DateTimeOffset before,DateTimeOffset now)
    {
        if(ids is null || ids.Count is <1 or >MaxIds || before.Offset!=TimeSpan.Zero || before>now ||
            ids.Any(id=>string.IsNullOrWhiteSpace(id)||id.Length>MaxIdLength||id!=id.Trim()||
                id.Any(char.IsControl)||id.IndexOfAny(['/', '\\', '%', ':', '?', '#'])>=0) ||
            ids.Distinct(StringComparer.Ordinal).Count()!=ids.Count)
            throw new ArgumentException("Invalid retention evaluation request.");
    }
    public Task<RetentionEvaluationResult?> ReadAsync(IReadOnlyList<string> ids,DateTimeOffset before,
        string sid,string session,CancellationToken cancellation=default)
        =>ReadAsync(ids,before,()=>(sid,session),cancellation);
    public Task<RetentionEvaluationResult?> ReadAsync(IReadOnlyList<string> ids,DateTimeOffset before,
        Func<(string Sid,string Session)> identity,CancellationToken cancellation=default)
    {
        var frozen=ids?.ToArray()!;ValidateRequest(frozen,before,_now());
        var started=DateTimeOffset.UtcNow;
        RetentionEvaluationResult Snapshot(string state)=>new(state,started,DateTimeOffset.UtcNow,null);
        return ArtifactMetadataWorker.ReadAsync(token=>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var owner=identity();token.ThrowIfCancellationRequested();
                var facts=_read(owner.Sid,owner.Session,Array.AsReadOnly(frozen),token);
                token.ThrowIfCancellationRequested();
                var requested=frozen.ToHashSet(StringComparer.Ordinal);
                if(facts is null || facts.Count!=frozen.Length || facts.Keys.Any(k=>!requested.Contains(k)) ||
                    frozen.Any(id=>!facts.TryGetValue(id,out var value)||value is not null&&value.Entry.RecordingId!=id))
                    throw new InvalidDataException("Evaluation evidence set differs.");
                var visible=frozen.Select(id=>facts[id]).OfType<OrdinaryRetentionEvidence>().ToArray();
                var page=_kernel.EvaluatePage(new(visible,null,frozen.Length,new("ordinary",CreatedBefore:before)),frozen.Length,before,token);
                var evaluated=page.Items.ToDictionary(e=>e.RecordingId!,StringComparer.Ordinal);
                var ordered=frozen.Select(id=>evaluated.TryGetValue(id,out var item)?item:
                    new RetentionPreviewEntry("ordinary:"+id,id,null,null,"protected","not_found_or_not_visible",false,null,null,null)).ToArray();
                token.ThrowIfCancellationRequested();
                return new RetentionEvaluationResult("complete",started,DateTimeOffset.UtcNow,
                    page with { Items=Array.AsReadOnly(ordered),EvaluatedCount=frozen.Length });
            }
            catch(OperationCanceledException){return Snapshot("cancelled");}
            catch {return Snapshot("failed");}
        },Snapshot,DeadlineForTests,WorkerExitedForTests,cancellation);
    }
}
