using System.Diagnostics;
using System.Security.Cryptography;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>Five bounded sequential reads sharing the main-media worker admission.</summary>
public sealed class OrdinaryAttachmentVerificationService
{
    public const long MaxBytes = 67_108_864;
    public const int DeadlineMs = 30_000, MaxFiles = 5, MaxDistinctAncestors = 64, MaxConcurrent = 1;
    public const string ObservationSemantics = "sequential_read_handle_point_in_time_comparisons_not_simultaneous_or_atomic_snapshot_earlier_files_may_change_before_response_not_future_integrity_guarantee";
    private static readonly string[] Names = ["metadata.json", "thumbnail.jpg", "first_frame.png", "last_frame.png", "marks.json"];
    private readonly Func<string, OrdinaryArtifactFacts?> _read;
    private readonly IReadOnlyPathMetadata _ancestors;
    private readonly IWindowsFileMetadataApi _native;
    internal Func<CancellationToken, Task> DeadlineForTests { get; set; } = token => Task.Delay(DeadlineMs, token);
    internal Func<FileStream, Memory<byte>, CancellationToken, ValueTask<int>> ReadForTests { get; set; } = (s,b,t) => s.ReadAsync(b,t);
    internal Action<FileStream>? OpenedForTests { get; set; }
    internal Action? ClosedForTests { get; set; }
    internal Action? WorkerExitedForTests { get; set; }

    public OrdinaryAttachmentVerificationService(SqliteOrdinaryRecordingHistoryStore writer)
        : this(writer.ReadArtifactFacts, ReadOnlyPathMetadata.Instance, WindowsFileMetadataApi.Instance) { }
    internal OrdinaryAttachmentVerificationService(Func<string, OrdinaryArtifactFacts?> read, IReadOnlyPathMetadata ancestors, IWindowsFileMetadataApi native)
    { _read=read; _ancestors=ancestors; _native=native; }

    public async Task<OrdinaryAttachmentVerificationResult?> VerifyAsync(string id, CancellationToken cancellation = default)
    {
        OrdinaryArtifactCheckService.ValidateRecordingId(id);
        var progress=new Progress(id);
        if(cancellation.IsCancellationRequested) return progress.Result("cancelled","verification_cancelled");
        if(!OrdinaryContentVerificationAdmission.TryAcquire()) return progress.Result("busy","verification_busy");
        var workerCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var timerCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<OrdinaryAttachmentVerificationResult?> work; Task deadline;
        try
        {
            // Dispatch, SQLite, ancestors, native calls, read and actual disposal all consume this deadline.
            deadline=DeadlineForTests(timerCancellation.Token);
            work=Task.Run(async () =>
            {
                try { return await Run(id,progress,workerCancellation.Token).ConfigureAwait(false); }
                finally
                {
                    workerCancellation.Dispose();
                    OrdinaryContentVerificationAdmission.Release();
                    WorkerExitedForTests?.Invoke();
                }
            });
        }
        catch { workerCancellation.Dispose(); OrdinaryContentVerificationAdmission.Release(); return progress.Result("failed","verification_failed"); }
        try
        {
            await Task.WhenAny(work,deadline).ConfigureAwait(false);
            if(cancellation.IsCancellationRequested || deadline.IsCompleted || progress.Expired)
            {
                // Freeze before signalling a worker which may resume immediately.
                var result=progress.Result(cancellation.IsCancellationRequested?"cancelled":"timeout",
                    cancellation.IsCancellationRequested?"verification_cancelled":"verification_deadline",freeze:true);
                try { workerCancellation.Cancel(); } catch(ObjectDisposedException) { }
                _=work.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
                return result;
            }
            return await work.ConfigureAwait(false);
        }
        finally { timerCancellation.Cancel(); }
    }

    private async Task<OrdinaryAttachmentVerificationResult?> Run(string id,Progress progress,CancellationToken cancellation)
    {
        try
        {
            void Check() { cancellation.ThrowIfCancellationRequested(); if(progress.Expired)throw new TimeoutException(); }
            Check(); var facts=_read(id);
            if(facts is null) {Check(); return null;}
            var row=facts.Media; var bundle=row.Bundle;
            progress.Facts(bundle); Check();
            OrdinaryAttachmentVerificationResult Skip(string reason) => progress.Result("completed",reason);
            if(row.Status!="completed" || row.CompletedAtUtc is null || !row.OutputPath.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase))
                return Skip("bundle_not_applicable");
            if(bundle is not { Status:"ready" }) return Skip("bundle_not_ready");
            if(bundle.ContentHashEvidence is not { } hashes) return Skip("recorded_hash_unavailable");
            if(bundle.IdentityEvidence is not { } identity || bundle.ContentsEvidence is not { } contents ||
                !hashes.Identity.Equals(identity) || identity.RecordingId!=id || identity.MediaPath!=row.OutputPath ||
                identity.BundlePath!=bundle.Path || hashes.Values.Count!=MaxFiles || identity.Anchors.Count!=MaxFiles ||
                contents.Contents.Count!=MaxFiles) return Skip("bundle_evidence_unknown");
            long expected=0;
            var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for(var i=0;i<MaxFiles;i++)
            {
                var item=contents.Contents[i]; var anchor=identity.Anchors[i];
                if(item.Name!=Names[i] || !anchor.Valid || anchor.Path!=Path.Combine(identity.BundlePath,Names[i]) ||
                    item.SizeBytes!=anchor.Length || !BundleMediaHashEvidence.ValidValue(hashes.Values[i])) return Skip("bundle_evidence_unknown");
                try { expected=checked(expected+anchor.Length); } catch(OverflowException) {return Skip("attachment_byte_limit");}
                var reason=OrdinaryArtifactMetadataSampler.PathReason(anchor.Path,out var ancestors);
                if(reason is not null) return Skip(reason);
                foreach(var parent in ancestors) if(parents.Add(parent) && parents.Count>MaxDistinctAncestors) return Skip("ancestor_check_limit");
            }
            if(expected>MaxBytes) return Skip("attachment_byte_limit");
            if(!_native.Supported || _native.DriveType(identity.BundlePath[..3]) is not (2 or 3 or 6)) return Skip("local_drive_unavailable");
            foreach(var parent in parents)
            {
                Check(); var attributes=_ancestors.GetAttributes(parent); Check();
                if((attributes&FileAttributes.ReparsePoint)!=0) return Skip("ancestor_reparse_point");
                if((attributes&FileAttributes.Directory)==0) return Skip("parent_not_directory");
                if(!WindowsReadOnlyFileMetadataProbe.ValidAttributes((uint)attributes)) return Skip("metadata_unavailable");
            }
            var buffer=new byte[65536]; long total=0;
            for(var i=0;i<MaxFiles;i++)
            {
                Check(); progress.Start(i);
                var anchor=identity.Anchors[i]; var path=anchor.Path; ObservedMediaHash? observed=null; string? fault=null;
                // Exactly one original object handle per item; no hash reopen by name.
                using(var handle=_native.Open(@"\\?\"+path,0x80000000|WindowsReadOnlyFileMetadataProbe.ReadAttributes,
                    WindowsReadOnlyFileMetadataProbe.ShareRead,WindowsReadOnlyFileMetadataProbe.OpenExisting,
                    0x00200000|0x08000000|0x40000000,out var error))
                {
                    Check();
                    if(handle.IsInvalid || handle.IsClosed) fault=error is 2 or 3?"file_missing":error is 5 or 32 or 33?"file_inaccessible":"metadata_unavailable";
                    else
                    {
                        var before=PublicationHandleObservation.Read(_native,handle,path); Check();
                        if(before is null) fault="unsafe_or_unknown_handle_metadata";
                        else if(before.Snapshot.Identity!=anchor.Identity) fault="publication_identity_mismatch";
                        else if(before.Snapshot.Length!=anchor.Length) fault="file_size_mismatch";
                        else
                        {
                            using(var stream=new FileStream(handle,FileAccess.Read,65536,isAsync:true))
                            using(var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                            {
                                OpenedForTests?.Invoke(stream); Check(); long itemBytes=0;
                                while(itemBytes<anchor.Length)
                                {
                                    Check();
                                    var requested=(int)Math.Min(buffer.Length,anchor.Length-itemBytes);
                                    var count=await ReadForTests(stream,buffer.AsMemory(0,requested),cancellation).ConfigureAwait(false);
                                    if(count<=0 || count>requested) {fault="read_length_mismatch"; break;}
                                    total=checked(total+count); itemBytes+=count; progress.Bytes(i,itemBytes,total);
                                    if(total>MaxBytes) {fault="attachment_byte_limit"; break;}
                                    Check(); hash.AppendData(buffer,0,count);
                                }
                                if(fault is null)
                                {
                                    var after=PublicationHandleObservation.Read(_native,stream.SafeFileHandle,path); Check();
                                    if(after is null || before!=after || stream.Position!=anchor.Length || stream.Length!=anchor.Length) fault="metadata_changed";
                                    else observed=new("sha256",Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),itemBytes);
                                }
                            }
                        }
                    }
                }
                ClosedForTests?.Invoke(); Check(); // No item succeeds before its actual handle is closed.
                var reasonCode=fault ?? (observed!.Value==hashes.Values[i]?"digest_match":"digest_mismatch");
                progress.Finish(i,reasonCode,observed);
                if(reasonCode!="digest_match") return progress.Result(fault is null?"completed":"failed",reasonCode);
            }
            Check(); return progress.Result("completed","digest_match");
        }
        catch(OrdinaryRecordingHistoryUnavailableException) {throw;}
        catch(OperationCanceledException) {return progress.Result("cancelled","verification_cancelled");}
        catch(TimeoutException) {return progress.Result("timeout","verification_deadline");}
        catch(Exception ex)
        {
            var reason=ex is FileNotFoundException or DirectoryNotFoundException?"file_missing":
                ex is UnauthorizedAccessException || ex is IOException && (ex.HResult&0xffff) is 5 or 32 or 33?"file_inaccessible":"verification_failed";
            return progress.Result("failed",reason);
        }
    }

    private sealed class Progress(string id)
    {
        private readonly object _gate=new();
        private readonly DateTimeOffset _started=DateTimeOffset.UtcNow;
        private readonly Stopwatch _clock=Stopwatch.StartNew();
        private OrdinaryAttachmentVerificationItem[] _items=[];
        private long _bytes; private bool _frozen;
        internal bool Expired=>_clock.ElapsedMilliseconds>=DeadlineMs;
        internal void Facts(OrdinaryRecordingBundleSnapshot? bundle)
        {
            lock(_gate) if(!_frozen) _items=Names.Select((name,i)=>new OrdinaryAttachmentVerificationItem(name,"not_checked","not_checked",
                "verification_not_started",0,bundle?.ContentHash(i),null,false,null,null)).ToArray();
        }
        internal void Start(int i) {lock(_gate)if(!_frozen)_items[i]=_items[i] with {CheckStartedAtUtc=DateTimeOffset.UtcNow};}
        internal void Bytes(int i,long itemBytes,long total) {lock(_gate)if(!_frozen){_bytes=total;_items[i]=_items[i] with {BytesRead=itemBytes};}}
        internal void Finish(int i,string reason,ObservedMediaHash? observed)
        {
            lock(_gate) if(!_frozen) _items[i]=_items[i] with { CheckState=observed is null?"failed":"completed",ReasonCode=reason,
                Comparison=observed is null?"not_checked":reason=="digest_match"?"match":"mismatch",ObservedHash=observed,
                ContentVerified=observed is not null && reason=="digest_match",CheckCompletedAtUtc=DateTimeOffset.UtcNow };
        }
        internal OrdinaryAttachmentVerificationResult Result(string state,string reason,bool freeze=false)
        {
            lock(_gate)
            {
                if(freeze)_frozen=true;
                var items=_items.Select(item=>item.CheckState=="not_checked" ? item with {ReasonCode=reason} : item).ToArray();
                var complete=state=="completed" && reason=="digest_match" && items.Length==MaxFiles && items.All(item=>item.ContentVerified);
                var comparison=complete?"match":items.Any(item=>item.Comparison=="mismatch")?"mismatch":"not_checked";
                return new(id,_started,DateTimeOffset.UtcNow,state,comparison,reason,_bytes,Array.AsReadOnly(items),complete);
            }
        }
    }
}
