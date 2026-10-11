using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>On-demand metadata only. One process/host lease survives API/service recreation.</summary>
public sealed class OrdinaryArtifactCheckService
{
    private static int _hostBusy;
    private readonly Func<string,OrdinaryArtifactFacts?> _read;
    private readonly OrdinaryArtifactMetadataSampler _sampler;
    internal Func<CancellationToken,Task> DeadlineForTests { get; set; } = token=>Task.Delay(TimeSpan.FromSeconds(3),token);
    internal Action? WorkerExitedForTests { get; set; }
    public OrdinaryArtifactCheckService(SqliteOrdinaryRecordingHistoryStore writer)
        : this(writer.ReadArtifactFacts,ReadOnlyPathMetadata.Instance,WindowsReadOnlyFileMetadataProbe.Instance) { }
    internal OrdinaryArtifactCheckService(Func<string,OrdinaryArtifactFacts?> read,IReadOnlyPathMetadata ancestors,IReadOnlyFileMetadataProbe leaf,Func<string,bool>? localDrive=null)
    { _read=read; _sampler=new(ancestors,leaf,localDrive); }
    public static void ValidateRecordingId(string id)
    { SqliteOrdinaryRecordingHistoryStore.ValidateRecordingId(id);if(id.Any(char.IsControl))throw new ArgumentException("Invalid recording id."); }

    public async Task<OrdinaryArtifactCheckResult?> ReadAsync(string id,CancellationToken cancellation=default)
    {
        ValidateRecordingId(id);
        var progress=new Progress(id);
        if(cancellation.IsCancellationRequested)return progress.Snapshot("cancelled");
        if(Interlocked.CompareExchange(ref _hostBusy,1,0)!=0)return progress.Snapshot("busy");
        var workCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var deadlineCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task deadline; Task<OrdinaryArtifactCheckResult?> work;
        try
        {
            // Start before DB dispatch, not after snapshot acquisition.
            deadline=DeadlineForTests(deadlineCancellation.Token);
            work=Task.Run(()=>
            {
                try { return Run(id,progress,workCancellation.Token); }
                finally
                {
                    workCancellation.Dispose(); Volatile.Write(ref _hostBusy,0);
                    WorkerExitedForTests?.Invoke();
                }
            });
        }
        catch
        { workCancellation.Dispose();Volatile.Write(ref _hostBusy,0);return progress.Snapshot("failed"); }
        try
        {
            if(await Task.WhenAny(work,deadline).ConfigureAwait(false)==work)return await work.ConfigureAwait(false);
            try {workCancellation.Cancel();}catch(ObjectDisposedException){ }
            // Never release the host lease here: synchronous native/SQLite work may still be running.
            _=work.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
            return progress.Snapshot(cancellation.IsCancellationRequested?"cancelled":"timeout");
        }
        finally { deadlineCancellation.Cancel(); }
    }

    private OrdinaryArtifactCheckResult? Run(string id,Progress progress,CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var facts=_read(id);cancellation.ThrowIfCancellationRequested();
            if(facts is null)return null;
            var row=facts.Media;
            var applicable=row.Status=="completed" && row.OutputPath.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase);
            var reason=!applicable?"media_not_applicable":row.CompletedAtUtc is null||row.OutputSizeBytes is not >0?"media_evidence_unknown":null;
            progress.Initialize(facts,reason);
            if(reason is not null)
            {
                // A suffix such as .mp4:stream must not hide an unsafe completed output.
                // This validation is lexical and performs no file IO.
                if(row.Status=="completed" && OrdinaryArtifactMetadataSampler.PathReason(row.OutputPath,out _) is { } invalid)
                    progress.Set(0,new("media",row.OutputSizeBytes,null,invalid=="path_invalid"?"unsafe_path":"unavailable",invalid,null,"unknown"));
                return progress.Snapshot("complete");
            }
            var plans=new List<(int Index,string Name,string Path,long Size,PublicationIdentityAnchor? Anchor,string[] Parents)>();
            var ancestorLimit=false;
            Add(0,"media",row.OutputPath,row.OutputSizeBytes!.Value,facts.Anchor);
            if(row.Bundle?.ContentsEvidence is { } evidence)
                for(var i=0;i<evidence.Contents.Count;i++)
                {var item=evidence.Contents[i];Add(i+1,item.Name,Path.Combine(row.Bundle.Path!,item.Name),item.SizeBytes,row.Bundle.IdentityEvidence?.Anchors[i]);}
            void Add(int index,string name,string path,long size,PublicationIdentityAnchor? anchor)
            {
                var invalid=OrdinaryArtifactMetadataSampler.PathReason(path,out var parents);
                if(invalid=="ancestor_check_limit")ancestorLimit=true;
                if(invalid is not null)
                    progress.Set(index,new(name,size,null,invalid=="path_invalid"?"unsafe_path":"unavailable",invalid,null,"unknown"));
                else plans.Add((index,name,path,size,anchor,parents));
            }
            if(ancestorLimit || plans.SelectMany(p=>p.Parents).Distinct(StringComparer.OrdinalIgnoreCase).Take(65).Count()>64)
            {
                foreach(var plan in plans)progress.Set(plan.Index,new(plan.Name,plan.Size,null,"unavailable","ancestor_check_limit",null,"unknown"));
                return progress.Snapshot("complete");
            }
            var drives=plans.Select(p=>p.Path[..3]).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(root=>root,root=>_sampler.SupportsDrive(root),StringComparer.OrdinalIgnoreCase);
            plans.RemoveAll(plan=>
            {
                if(drives[plan.Path[..3]])return false;
                progress.Set(plan.Index,new(plan.Name,plan.Size,null,"unavailable","local_drive_unavailable",null,"unknown"));return true;
            });
            var cache=new Dictionary<string,(string Observation,string Reason)?>(StringComparer.OrdinalIgnoreCase);
            foreach(var plan in plans)
            {
                cancellation.ThrowIfCancellationRequested();progress.Begin(plan.Index);
                var sampled=_sampler.Sample(plan.Name,plan.Path,plan.Size,plan.Anchor,plan.Parents,cache,cancellation);
                progress.Set(plan.Index,sampled);
            }
            cancellation.ThrowIfCancellationRequested();return progress.Snapshot("complete");
        }
        catch(OperationCanceledException){return progress.Snapshot("cancelled");}
    }

    private sealed class Progress(string id)
    {
        private readonly object _gate=new();private readonly DateTimeOffset _started=DateTimeOffset.UtcNow;
        private string? _status,_bundleStatus;private int? _version;
        private OrdinaryArtifactFileCheck _media=new("media",null,null,"not_checked","not_checked",null,"unknown");
        private OrdinaryArtifactFileCheck[]? _items;
        internal void Initialize(OrdinaryArtifactFacts facts,string? ineligible)
        {
            lock(_gate)
            {
                _status=facts.Media.Status;_bundleStatus=facts.Media.Bundle?.Status;
                _media=new("media",facts.Media.OutputSizeBytes,null,
                    ineligible=="media_not_applicable"?"not_applicable":ineligible=="media_evidence_unknown"?"unknown":"not_checked",
                    ineligible??"not_checked",null,"unknown");
                if(facts.Media.Bundle?.ContentsEvidence is { } evidence)
                {
                    _version=1;_items=evidence.Contents.Select(i=>new OrdinaryArtifactFileCheck(i.Name,i.SizeBytes,null,
                        "not_checked",ineligible??"not_checked",null,"unknown")).ToArray();
                }
            }
        }
        internal void Set(int index,OrdinaryArtifactFileCheck check)
        {lock(_gate){if(index==0)_media=check;else _items![index-1]=check;}}
        internal void Begin(int index)
        {lock(_gate){var check=index==0?_media:_items![index-1];Set(index,check with {ReasonCode="probe_in_progress",SampledAtUtc=DateTimeOffset.UtcNow});}}
        internal OrdinaryArtifactCheckResult Snapshot(string state)
        {
            lock(_gate)
            {
                OrdinaryArtifactFileCheck Freeze(OrdinaryArtifactFileCheck check)=>
                    check.Observation!="not_checked"||state=="complete"?check:check with {
                        Observation=check.ReasonCode=="probe_in_progress"?"unavailable":"not_checked",
                        ReasonCode="probe_"+state };
                return new(id,_started,DateTimeOffset.UtcNow,state,_status,Freeze(_media),
                    new(_bundleStatus,_version,_items?.Select(Freeze)));
            }
        }
    }
}
