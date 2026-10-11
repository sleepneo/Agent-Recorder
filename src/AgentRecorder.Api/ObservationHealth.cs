using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Api;

internal enum HealthState { Ready, Available, Unavailable, Unsupported, Unknown, NotChecked, Idle, Listening, Stopped, NotStarted }
internal enum HealthProbeState { Complete, Timeout, Busy, Failed, Cancelled, NotChecked }
internal sealed record HealthHotkey(bool Supported, bool Registered, string? Gesture, string Policy, bool Expected, int ErrorCode);
internal sealed record HealthTool(HealthState State, string? Path, string? Source, string Reason, DateTimeOffset? SampledAt = null);
internal sealed record HealthDesktop(HealthState State, string Reason, DateTimeOffset? SampledAt = null);
internal sealed record ObservationHealthOwner(ApiRuntimeObservation? Api, RuntimeReadinessObservation? Readiness,
    string? DataDirectory, HealthHotkey Stop, HealthHotkey Chapter, DateTimeOffset SampledAt,
    string WindowPolicy, string DisplayPolicy, string RegionPolicy);
internal sealed record ObservationHealthSnapshot(ObservationHealthOwner Owner, HealthTool Ffmpeg, HealthTool Ffprobe,
    HealthTool WgcHelper, HealthTool AudioHelper, HealthDesktop Desktop, HealthProbeState ProbeState)
{
    internal static ObservationHealthSnapshot Partial(ObservationHealthOwner owner, HealthProbeState state) =>
        new(owner, new(HealthState.NotChecked,null,null,"not_checked"),new(HealthState.NotChecked,null,null,"not_checked"),
            new(HealthState.NotChecked,null,null,"not_checked"),new(HealthState.NotChecked,null,null,"not_checked"),
            new(HealthState.Unknown,"not_checked"),state);
}
internal interface IObservationHealthGateway
{
    // Implementations without an asynchronous owner dispatcher cannot be read from HTTP workers.
    Task<ObservationHealthSnapshot> ReadAsync(CancellationToken cancellation) =>
        Task.FromResult(ObservationHealthSnapshot.Partial(UnavailableObservationHealthGateway.Instance.CaptureOwner(),HealthProbeState.NotChecked));
    ObservationHealthSnapshot PartialSnapshot(HealthProbeState state)=>ObservationHealthSnapshot.Partial(UnavailableObservationHealthGateway.Instance.CaptureOwner(),state);
    ObservationHealthOwner CaptureOwner();
    Task<ObservationHealthSnapshot> SampleAsync(ObservationHealthOwner owner, CancellationToken cancellation);
}

// One shared host gate survives closing/reopening windows. Cancellation stops only
// work between probes: an uncancellable native call retains the gate until it ends.
internal sealed class ObservationHealthGateway : IObservationHealthGateway
{
    private readonly Func<ObservationHealthOwner> _owner;
    private readonly ObservationHealthProbes _probes;
    private int _busy;
    private readonly Func<CancellationToken,Task<ObservationHealthOwner>>? _postOwner;
    private readonly Func<ObservationHealthOwner> _partialOwner;
    internal Func<CancellationToken,Task> DeadlineForTests { get; set; } = token => Task.Delay(TimeSpan.FromSeconds(3),token);
    internal ObservationHealthGateway(Func<ObservationHealthOwner> owner, ObservationHealthProbes? probes = null,
        Func<CancellationToken,Task<ObservationHealthOwner>>? postOwner=null, Func<ObservationHealthOwner>? partialOwner=null)
    { _owner=owner; _probes=probes??new(); _postOwner=postOwner; _partialOwner=partialOwner??UnavailableObservationHealthGateway.Instance.CaptureOwner; }
    private ObservationHealthOwner PartialOwner()
    { try { return _partialOwner(); } catch { return UnavailableObservationHealthGateway.Instance.CaptureOwner(); } }
    public async Task<ObservationHealthSnapshot> ReadAsync(CancellationToken cancellation)
    {
        var progress=new OwnerProgress(PartialOwner());
        if(cancellation.IsCancellationRequested) return ObservationHealthSnapshot.Partial(progress.Owner,HealthProbeState.Failed);
        if(_postOwner is null) return ObservationHealthSnapshot.Partial(progress.Owner,HealthProbeState.NotChecked);
        if(Interlocked.CompareExchange(ref _busy,1,0)!=0) return ObservationHealthSnapshot.Partial(progress.Owner,HealthProbeState.Busy);
        var workCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var deadlineCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // The stopwatch starts before owner dispatch; there is one deadline for the whole operation.
        var deadline=DeadlineForTests(deadlineCancellation.Token);
        var work=ReadCoreAsync(progress,workCancellation);
        try
        {
            var winner=await Task.WhenAny(work,deadline).ConfigureAwait(false);
            if(winner==work) return await work.ConfigureAwait(false);
            try {workCancellation.Cancel();} catch(ObjectDisposedException) { }
            return ObservationHealthSnapshot.Partial(progress.Owner,cancellation.IsCancellationRequested?HealthProbeState.Failed:HealthProbeState.Timeout);
        }
        finally { deadlineCancellation.Cancel(); }
    }
    private sealed class OwnerProgress(ObservationHealthOwner owner)
    {
        private ObservationHealthOwner _owner=owner;
        internal ObservationHealthOwner Owner { get=>Volatile.Read(ref _owner); set=>Volatile.Write(ref _owner,value); }
    }
    private async Task<ObservationHealthSnapshot> ReadCoreAsync(OwnerProgress progress,CancellationTokenSource cancellation)
    {
        try
        {
            // Do not WaitAsync/cancel this await: a queued owner callback still owns the quota
            // until it drains or the dispatcher's owner handle is destroyed.
            progress.Owner=await _postOwner!(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return await Task.Run(()=>SampleCore(progress.Owner,cancellation.Token)).ConfigureAwait(false);
        }
        catch { return ObservationHealthSnapshot.Partial(progress.Owner,HealthProbeState.Failed); }
        finally { cancellation.Dispose(); Volatile.Write(ref _busy,0); }
    }
    public ObservationHealthSnapshot PartialSnapshot(HealthProbeState state)=>ObservationHealthSnapshot.Partial(PartialOwner(),state);
    public ObservationHealthOwner CaptureOwner() => _owner();
    public Task<ObservationHealthSnapshot> SampleAsync(ObservationHealthOwner owner, CancellationToken cancellation)
    {
        if(Interlocked.CompareExchange(ref _busy,1,0)!=0)
            return Task.FromResult(ObservationHealthSnapshot.Partial(owner,HealthProbeState.Busy));
        return Task.Run(() =>
        {
            try { return SampleCore(owner,cancellation); }
            finally { Volatile.Write(ref _busy,0); }
        });
    }
    private ObservationHealthSnapshot SampleCore(ObservationHealthOwner owner,CancellationToken cancellation)
    {
        try
        {
            var ffmpeg=Tool(_probes.Ffmpeg,_probes.FfmpegSource,cancellation);
            var ffprobe=Tool(_probes.Ffprobe,_probes.FfmpegSource,cancellation);
            var wgc=Tool(_probes.WgcHelper,()=>"WgcHelperExePathResolver",cancellation);
            var audio=Tool(_probes.AudioHelper,()=>"AudioHelperExePathResolver",cancellation);
            cancellation.ThrowIfCancellationRequested();
            HealthDesktop desktop;
            try
            {
                var observed=_probes.Desktop();
                var reason=SafeDesktopReason(observed.FailureCode);
                desktop=new(reason=="desktop_observation_failed"?HealthState.Unknown:
                    observed.IsOnInteractiveDesktop?HealthState.Available:HealthState.Unavailable,reason,DateTimeOffset.UtcNow);
            }
            catch { desktop=new(HealthState.Unknown,"desktop_observation_failed",DateTimeOffset.UtcNow); }
            return new(owner,ffmpeg,ffprobe,wgc,audio,desktop,HealthProbeState.Complete);
        }
        catch(OperationCanceledException) { return ObservationHealthSnapshot.Partial(owner,HealthProbeState.Cancelled); }
        catch { return ObservationHealthSnapshot.Partial(owner,HealthProbeState.Failed); }
    }
    private HealthTool Tool(Func<string?> resolve,Func<string?> source,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var resolved=resolve(); var path=resolved is null?null:Path.GetFullPath(resolved); var origin=source();
            var found=path is not null && _probes.FileExists(path);
            return new(found?HealthState.Available:HealthState.Unavailable,
                path,origin,found?"path_only":"tool_not_found",DateTimeOffset.UtcNow);
        }
        catch(Exception ex) when(ex is FileNotFoundException || ex is ApiException { Code:"ENCODER_ERROR" })
        { return new(HealthState.Unavailable,null,null,"tool_not_found",DateTimeOffset.UtcNow); }
        catch { return new(HealthState.Unknown,null,null,"tool_resolution_failed",DateTimeOffset.UtcNow); }
    }
    internal static string SafeDesktopReason(string reason) => reason switch
    {
        ""=>"interactive_desktop", "DESKTOP_LOCKED"=>"desktop_locked", "WINDOWS_REQUIRED"=>"windows_required",
        "INPUT_DESKTOP_UNAVAILABLE"=>"input_desktop_unavailable", "NO_ACTIVE_INTERACTIVE_SESSION"=>"session_unavailable",
        "INTERACTIVE_DESKTOP_REQUIRED"=>"interactive_desktop_required",
        "USER_IDENTITY_UNAVAILABLE" or "ACTIVE_USER_UNAVAILABLE" or "ACTIVE_SESSION_UNAVAILABLE" or "INTERACTIVE_USER_MISMATCH"=>"session_unavailable",
        _=>"desktop_observation_failed"
    };
    internal static string Policy(string? value) => string.IsNullOrEmpty(value)?"default":
        value==CaptureBackendSelector.WgcContinuousBackend?"wgc-continuous":"unknown";
}
internal sealed class ObservationHealthProbes
{
    internal Func<string?> Ffmpeg { get; init; }=()=>FfmpegLocator.FfmpegPath;
    internal Func<string?> Ffprobe { get; init; }=()=>FfmpegLocator.FfprobePath;
    internal Func<string?> FfmpegSource { get; init; }=()=>FfmpegLocator.Source;
    internal Func<string?> WgcHelper { get; init; }=WgcHelperExePathResolver.Resolve;
    internal Func<string?> AudioHelper { get; init; }=AudioHelperExePathResolver.Resolve;
    internal Func<string,bool> FileExists { get; init; }=File.Exists;
    internal Func<InteractiveDesktopObservation> Desktop { get; init; }=InteractiveDesktopRuntime.ObserveCurrent;
}
internal sealed class UnavailableObservationHealthGateway : IObservationHealthGateway
{
    internal static readonly UnavailableObservationHealthGateway Instance=new();
    public ObservationHealthOwner CaptureOwner()=>new(null,null,null,new(false,false,null,"unknown",false,0),
        new(false,false,null,"unknown",false,0),DateTimeOffset.UtcNow,"unknown","unknown","unknown");
    public Task<ObservationHealthSnapshot> SampleAsync(ObservationHealthOwner owner,CancellationToken cancellation)=>
        Task.FromResult(ObservationHealthSnapshot.Partial(owner,HealthProbeState.NotChecked));
}
