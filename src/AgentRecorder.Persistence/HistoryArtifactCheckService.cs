using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

internal sealed record HistoryArtifactFacts(string EntryId,string Source,string Status,string? Path,long? Size,PublicationIdentityAnchor? Anchor);
public sealed record HistoryArtifactCheckResult(string EntryId,string Source,DateTimeOffset CheckStartedAtUtc,
    DateTimeOffset CheckCompletedAtUtc,string ProbeState,string? MediaHistoryStatus,OrdinaryArtifactFileCheck Media);

/// <summary>Explicit, read-only point-in-time metadata for an identity-bound plan or future entry.</summary>
public sealed class HistoryArtifactCheckService
{
    private readonly Func<string,string,string,HistoryArtifactFacts?> _read;
    private readonly OrdinaryArtifactMetadataSampler _sampler;
    internal Func<CancellationToken,Task> DeadlineForTests {get;set;}=token=>Task.Delay(TimeSpan.FromSeconds(3),token);
    internal Action? WorkerExitedForTests {get;set;}
    public HistoryArtifactCheckService(SqliteOperationalStore store)
        :this(new HistoryArtifactFactsReader(store).Read,ReadOnlyPathMetadata.Instance,WindowsReadOnlyFileMetadataProbe.Instance) { }
    internal HistoryArtifactCheckService(Func<string,string,string,HistoryArtifactFacts?> read,IReadOnlyPathMetadata ancestors,
        IReadOnlyFileMetadataProbe leaf,Func<string,bool>? drive=null)
    { _read=read; _sampler=new(ancestors,leaf,drive); }

    public static (string Source,string Id) ParseEntryId(string entryId)
    {
        if(entryId is null)throw new ArgumentException("Invalid history entry id.");
        var split=entryId.IndexOf(':');
        if(split<0)throw new ArgumentException("Invalid history entry id.");
        var source=entryId[..split]; var id=entryId[(split+1)..];
        const string recurringPrefix="recurring-occurrence/v1:";
        var recurring=source=="plan" && id.Length==recurringPrefix.Length+64 && id.StartsWith(recurringPrefix,StringComparison.Ordinal) &&
            id[recurringPrefix.Length..].All(ch=>ch is >= '0' and <= '9' or >= 'a' and <= 'f');
        if(source is not ("plan" or "future_window") || !UnifiedHistoryQueryService.ValidId(id) ||
            id.Any(char.IsControl) || (!recurring && id.IndexOfAny([':', '/', '\\', '%', '?', '#'])>=0))
            throw new ArgumentException("Invalid history entry id.");
        return (source,id);
    }

    public Task<HistoryArtifactCheckResult?> ReadAsync(string entryId,string sid,string session,CancellationToken cancellation=default)
    {
        var (source,_)=ParseEntryId(entryId);
        var progress=new Progress(entryId,source);
        return ArtifactMetadataWorker.ReadAsync(token=>Run(entryId,sid,session,progress,token),progress.Snapshot,
            DeadlineForTests,WorkerExitedForTests,cancellation);
    }

    private HistoryArtifactCheckResult? Run(string entryId,string sid,string session,Progress progress,CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var facts=_read(entryId,sid,session); cancellation.ThrowIfCancellationRequested();
            if(facts is null)return null;
            var applicable=facts.Status=="completed" && facts.Path?.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase)==true;
            var reason=facts.Status!="completed"?"media_not_applicable":!applicable || facts.Size is not >0?"media_evidence_unknown":null;
            progress.Initialize(facts,reason);
            if(reason is not null)
            {
                if(facts.Status=="completed" && facts.Path is not null && OrdinaryArtifactMetadataSampler.PathReason(facts.Path,out _) is {} unsafeReason)
                    progress.Set(new("media",facts.Size,null,unsafeReason=="path_invalid"?"unsafe_path":"unavailable",unsafeReason,null,"unknown"));
                return progress.Snapshot("complete");
            }
            var invalid=OrdinaryArtifactMetadataSampler.PathReason(facts.Path!,out var parents);
            if(invalid is not null)progress.Set(new("media",facts.Size,null,invalid=="path_invalid"?"unsafe_path":"unavailable",invalid,null,"unknown"));
            else if(!_sampler.SupportsDrive(facts.Path![..3]))
                progress.Set(new("media",facts.Size,null,"unavailable","local_drive_unavailable",null,"unknown"));
            else
            {
                cancellation.ThrowIfCancellationRequested(); progress.Begin();
                progress.Set(_sampler.Sample("media",facts.Path!,facts.Size!.Value,facts.Anchor,parents,
                    new(StringComparer.OrdinalIgnoreCase),cancellation,requireSingleLink:true));
            }
            cancellation.ThrowIfCancellationRequested();return progress.Snapshot("complete");
        }
        catch(OperationCanceledException){return progress.Snapshot("cancelled");}
    }

    private sealed class Progress(string entryId,string source)
    {
        private readonly object _gate=new(); private readonly DateTimeOffset _started=DateTimeOffset.UtcNow;
        private string? _status;
        private OrdinaryArtifactFileCheck _media=new("media",null,null,"not_checked","not_checked",null,"unknown");
        internal void Initialize(HistoryArtifactFacts facts,string? reason)
        {lock(_gate){_status=facts.Status;_media=new("media",facts.Size,null,reason=="media_evidence_unknown"?"unknown":"not_checked",reason??"not_checked",null,"unknown");}}
        internal void Set(OrdinaryArtifactFileCheck check){lock(_gate)_media=check;}
        internal void Begin(){lock(_gate)_media=_media with {ReasonCode="probe_in_progress",SampledAtUtc=DateTimeOffset.UtcNow};}
        internal HistoryArtifactCheckResult Snapshot(string state)
        {
            lock(_gate)
            {
                var media=_media.Observation!="not_checked" || state=="complete"?_media:_media with {
                    Observation=_media.ReasonCode=="probe_in_progress"?"unavailable":"not_checked",ReasonCode="probe_"+state};
                return new(entryId,source,_started,DateTimeOffset.UtcNow,state,_status,media);
            }
        }
    }
}
