using System.Diagnostics;
using System.Security.Cryptography;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>One nonqueued process-wide worker, including during timed-out native/DB work and gateway recreation.</summary>
public sealed class OrdinaryMediaVerificationService
{
    public const long MaxBytes = 1_073_741_824;
    public const int DeadlineMs = 30_000, MaxConcurrent = 1;
    public const string ObservationSemantics = "single_read_handle_point_in_time_comparison_not_future_integrity_guarantee";
    private readonly Func<string, OrdinaryArtifactFacts?> _read;
    private readonly IReadOnlyPathMetadata _ancestors;
    private readonly IWindowsFileMetadataApi _native;
    internal Func<CancellationToken, Task> DeadlineForTests { get; set; } = token => Task.Delay(DeadlineMs, token);
    internal Func<FileStream, Memory<byte>, CancellationToken, ValueTask<int>> ReadForTests { get; set; } = (s,b,t) => s.ReadAsync(b,t);
    internal Action<FileStream>? OpenedForTests { get; set; }
    internal Action? WorkerExitedForTests { get; set; }
    public OrdinaryMediaVerificationService(SqliteOrdinaryRecordingHistoryStore writer)
        : this(writer.ReadArtifactFacts, ReadOnlyPathMetadata.Instance, WindowsFileMetadataApi.Instance) { }
    internal OrdinaryMediaVerificationService(Func<string, OrdinaryArtifactFacts?> read, IReadOnlyPathMetadata ancestors, IWindowsFileMetadataApi native)
    { _read = read; _ancestors = ancestors; _native = native; }

    public async Task<OrdinaryMediaVerificationResult?> VerifyAsync(string id, CancellationToken cancellation = default)
    {
        OrdinaryArtifactCheckService.ValidateRecordingId(id);
        var progress = new Progress(id);
        if(cancellation.IsCancellationRequested) return progress.Result("cancelled", "verification_cancelled");
        if(!OrdinaryContentVerificationAdmission.TryAcquire()) return progress.Result("busy", "verification_busy");
        var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task<OrdinaryMediaVerificationResult?> work;
        Task deadline;
        try
        {
            // Includes synchronous snapshot dispatch, native calls, actual reads and handle disposal.
            deadline = DeadlineForTests(timerCancellation.Token);
            work = Task.Run(async () =>
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
                try { workerCancellation.Cancel(); } catch(ObjectDisposedException) { }
                _=work.ContinueWith(t=>{_ = t.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
                return progress.Result(cancellation.IsCancellationRequested?"cancelled":"timeout",
                    cancellation.IsCancellationRequested?"verification_cancelled":"verification_deadline");
            }
            return await work.ConfigureAwait(false);
        }
        finally { timerCancellation.Cancel(); }
    }

    private async Task<OrdinaryMediaVerificationResult?> Run(string id, Progress progress, CancellationToken cancellation)
    {
        try
        {
            void Check() { cancellation.ThrowIfCancellationRequested(); if(progress.Expired)throw new TimeoutException(); }
            Check(); var facts = _read(id); Check();
            if(facts is null) return null;
            var row=facts.Media;
            progress.Recorded(row.MediaHash);
            OrdinaryMediaVerificationResult Skip(string reason)=>progress.Result("completed",reason);
            if(row.Status!="completed" || !row.OutputPath.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase)) return Skip("media_not_applicable");
            if(row.MediaHash is null) return Skip("recorded_hash_unavailable");
            if(facts.Anchor is not { Valid:true } anchor) return Skip("publication_anchor_unavailable");
            if(row.CompletedAtUtc is null || row.OutputSizeBytes!=anchor.Length || row.MediaHash.SizeBytes!=anchor.Length ||
                !string.Equals(row.OutputPath,anchor.Path,StringComparison.OrdinalIgnoreCase)) return Skip("media_evidence_unknown");
            if(anchor.Length>MaxBytes) return Skip("media_byte_limit");
            var pathReason=OrdinaryArtifactMetadataSampler.PathReason(row.OutputPath,out var parents);
            if(pathReason is not null) return Skip(pathReason);
            if(!_native.Supported || _native.DriveType(row.OutputPath[..3]) is not (2 or 3 or 6)) return Skip("local_drive_unavailable");
            foreach(var parent in parents)
            {
                Check(); var attributes=_ancestors.GetAttributes(parent);
                if((attributes&FileAttributes.ReparsePoint)!=0) return Skip("ancestor_reparse_point");
                if((attributes&FileAttributes.Directory)==0) return Skip("parent_not_directory");
                if(!WindowsReadOnlyFileMetadataProbe.ValidAttributes((uint)attributes)) return Skip("metadata_unavailable");
            }
            Check();
            ObservedMediaHash observed;
            // OPEN_REPARSE_POINT prevents following the leaf; overlapped for genuine cancellation-aware reads.
            using(var handle=_native.Open(@"\\?\"+row.OutputPath,0x80000000|WindowsReadOnlyFileMetadataProbe.ReadAttributes,
                WindowsReadOnlyFileMetadataProbe.ShareRead,WindowsReadOnlyFileMetadataProbe.OpenExisting,
                0x00200000|0x08000000|0x40000000,out var error))
            {
                Check();
                if(handle.IsInvalid || handle.IsClosed) return Skip(error is 2 or 3?"file_missing":error is 5 or 32 or 33?"file_inaccessible":"metadata_unavailable");
                var before=PublicationHandleObservation.Read(_native,handle,row.OutputPath); Check();
                if(before is null) return Skip("unsafe_or_unknown_handle_metadata");
                if(before.Snapshot.Identity!=anchor.Identity) return Skip("publication_identity_mismatch");
                if(before.Snapshot.Length!=anchor.Length) return Skip("file_size_mismatch");
                using(var stream=new FileStream(handle,FileAccess.Read,65536,isAsync:true))
                using(var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    OpenedForTests?.Invoke(stream); Check();
                    var buffer=new byte[65536]; long total=0;
                    while(total<anchor.Length)
                    {
                        Check();
                        var count=await ReadForTests(stream,buffer.AsMemory(0,(int)Math.Min(buffer.Length,anchor.Length-total)),cancellation).ConfigureAwait(false);
                        if(count<=0 || count>Math.Min(buffer.Length,anchor.Length-total)) return Skip("read_length_mismatch");
                        total+=count;progress.Bytes(total); Check(); hash.AppendData(buffer,0,count);
                    }
                    var after=PublicationHandleObservation.Read(_native,stream.SafeFileHandle,row.OutputPath); Check();
                    if(after is null || before!=after || stream.Position!=anchor.Length || stream.Length!=anchor.Length) return Skip("metadata_changed");
                    observed=new("sha256",Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),total);
                }
            }
            // Disposing the stream and handle is part of the worker, not deferred past a verified response.
            Check();
            return progress.Result("completed",observed.Value==row.MediaHash.Value?"digest_match":"digest_mismatch",observed);
        }
        catch(OrdinaryRecordingHistoryUnavailableException) { throw; }
        catch(OperationCanceledException) { return progress.Result("cancelled","verification_cancelled"); }
        catch(TimeoutException) { return progress.Result("timeout","verification_deadline"); }
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
        private OrdinaryMediaHashSnapshot? _recorded;
        private long _bytes;
        internal bool Expired=>_clock.ElapsedMilliseconds>=DeadlineMs;
        internal void Recorded(OrdinaryMediaHashSnapshot? hash) { lock(_gate)_recorded=hash; }
        internal void Bytes(long value) { lock(_gate)_bytes=value; }
        internal OrdinaryMediaVerificationResult Result(string state,string reason,ObservedMediaHash? observed=null)
        {
            lock(_gate) return new(id,_started,DateTimeOffset.UtcNow,state,
                observed is null?"not_checked":reason=="digest_match"?"match":"mismatch",reason,
                _bytes,_recorded,observed,observed is not null && state=="completed" && reason=="digest_match");
        }
    }
}
