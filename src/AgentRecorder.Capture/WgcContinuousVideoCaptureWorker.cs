using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Adapts the existing proof-bearing WGC backend to the split A/V video-worker
/// contract. WGC publishes a video-only temporary MP4; AvSplit remains the
/// sole owner of audio/video convergence and final AAC muxing.
/// </summary>
internal sealed class WgcContinuousVideoCaptureWorker : IVideoCaptureWorker
{
    private readonly CaptureAuthorizationProof _authorizationProof;
    private readonly Func<WgcContinuousCaptureBackend> _backendFactory;
    private readonly Func<long> _timestampProvider;
    private readonly TaskCompletionSource<object?> _exitSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WgcContinuousCaptureBackend? _backend;
    private OutputMeta? _terminalMeta;
    private int _exitCode = -1;
    private int _hasExited;
    private int _manualStop;
    private int _firstFrameSeen;
    private long _launchAnchorTicks;
    private long _firstFrameAnchorTicks;
    private long _videoMediaStartAnchorTicks;
    private long _videoMediaStartSourceTimeHns;
    private long _firstProgressFrame = -1;
    private long _firstProgressOutTimeUs = -1;

    public WgcContinuousVideoCaptureWorker(CaptureAuthorizationProof authorizationProof)
        : this(authorizationProof, () => new WgcContinuousCaptureBackend(), Stopwatch.GetTimestamp)
    {
    }

    internal WgcContinuousVideoCaptureWorker(
        CaptureAuthorizationProof authorizationProof,
        Func<WgcContinuousCaptureBackend> backendFactory,
        Func<long> timestampProvider)
    {
        _authorizationProof = authorizationProof ?? throw new ArgumentNullException(nameof(authorizationProof));
        _backendFactory = backendFactory ?? throw new ArgumentNullException(nameof(backendFactory));
        _timestampProvider = timestampProvider ?? throw new ArgumentNullException(nameof(timestampProvider));
    }

    public event Action<FirstFrameObservation>? FirstFrameObserved;
    public event Action<int, string>? NaturalExit;

    public string? OutputPath { get; private set; }
    internal bool HasBackendForTests => _backend != null;
    public int ExitCode => Volatile.Read(ref _exitCode);
    public bool HasExited => Volatile.Read(ref _hasExited) != 0;
    public long LaunchAnchorTicks => Interlocked.Read(ref _launchAnchorTicks);
    public long FirstFrameAnchorTicks => Interlocked.Read(ref _firstFrameAnchorTicks);
    public long? VideoMediaStartAnchorTicks => Interlocked.Read(ref _videoMediaStartAnchorTicks) > 0
        ? Interlocked.Read(ref _videoMediaStartAnchorTicks) : null;
    public bool RequiresVideoMediaStartAnchor => true;
    public long? VideoMediaStartSourceTimeHns => Interlocked.Read(ref _videoMediaStartSourceTimeHns) > 0
        ? Interlocked.Read(ref _videoMediaStartSourceTimeHns) : null;
    public long? FirstProgressFrame => Interlocked.Read(ref _firstProgressFrame) is var frame && frame >= 0 ? frame : null;
    public long? FirstProgressOutTimeUs => Interlocked.Read(ref _firstProgressOutTimeUs) is var time && time >= 0 ? time : null;
    public double? ProgressAnchorDeltaMs => null;

    public void Start(CaptureConfig cfg, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (!cfg.RequireWindowSurface || !string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal))
            throw new InvalidOperationException("WGC video worker requires an explicit window_surface window target.");
        if (_backend != null)
            throw new InvalidOperationException("WGC video worker has already been started.");

        var targetFailure = WgcWindowSurfaceTargetValidator.Validate(cfg);
        if (targetFailure != null)
            throw new InvalidOperationException("window_surface target revalidation failed: " + targetFailure);

        var backend = _backendFactory();
        _backend = backend;
        OutputPath = outputPath;
        backend.OnNaturalExit(OnBackendNaturalExit);
        backend.FirstFrameObserved += OnBackendFirstFrame;
        Interlocked.Exchange(ref _launchAnchorTicks, _timestampProvider());
        try
        {
            backend.Start(CreateVideoOnlyConfig(cfg, outputPath), _authorizationProof);
        }
        catch
        {
            // A synchronous WGC start failure must not leave this adapter
            // retaining a half-started backend or reporting a live worker to
            // AvSplit. The backend owns rollback of any staging directory;
            // Dispose is an idempotent final safety net for its session seam.
            try { backend.FirstFrameObserved -= OnBackendFirstFrame; } catch { }
            try { backend.Dispose(); } catch { }
            if (ReferenceEquals(_backend, backend))
                _backend = null;
            OutputPath = null;
            Interlocked.Exchange(ref _exitCode, -1);
            Interlocked.Exchange(ref _hasExited, 1);
            _exitSignal.TrySetResult(null);
            throw;
        }
    }

    public OutputMeta Stop()
    {
        Interlocked.Exchange(ref _manualStop, 1);
        var backend = _backend;
        if (backend == null)
        {
            Interlocked.Exchange(ref _hasExited, 1);
            _exitSignal.TrySetResult(null);
            return _terminalMeta ?? new OutputMeta();
        }

        try
        {
            var meta = backend.Stop();
            _terminalMeta ??= meta;
            _exitCode = backend.ExitCode;
            Interlocked.Exchange(ref _hasExited, 1);
            _exitSignal.TrySetResult(null);
            return meta;
        }
        catch
        {
            Interlocked.Exchange(ref _exitCode, -1);
            Interlocked.Exchange(ref _hasExited, 1);
            _exitSignal.TrySetResult(null);
            throw;
        }
    }

    public string GetStderrLog() => _terminalMeta?.StderrLog ?? string.Empty;

    public bool WaitForExit(TimeSpan timeout)
    {
        try { return _exitSignal.Task.Wait(timeout); }
        catch { return false; }
    }

    public void Dispose()
    {
        var backend = Interlocked.Exchange(ref _backend, null);
        if (backend == null) return;
        try { backend.FirstFrameObserved -= OnBackendFirstFrame; } catch { }
        try { backend.Dispose(); } catch { }
        if (!HasExited)
        {
            Interlocked.Exchange(ref _hasExited, 1);
            _exitSignal.TrySetResult(null);
        }
    }

    private void OnBackendFirstFrame(FirstFrameObservation observation)
    {
        if (Interlocked.Exchange(ref _firstFrameSeen, 1) != 0)
            return;
        Interlocked.Exchange(ref _firstFrameAnchorTicks, _timestampProvider());
        if (observation.MediaStartSystemRelativeTimeHns is long sourceTimeHns)
        {
            if (sourceTimeHns > 0)
                Interlocked.Exchange(ref _videoMediaStartSourceTimeHns, sourceTimeHns);
            try
            {
                Interlocked.Exchange(ref _videoMediaStartAnchorTicks,
                    MediaAnchorHelper.FromSystemRelativeTimeHns(sourceTimeHns));
            }
            catch (OverflowException)
            {
                Interlocked.Exchange(ref _videoMediaStartAnchorTicks, 0);
            }
        }
        Interlocked.Exchange(ref _firstProgressFrame, observation.FrameNumber);
        if (observation.OutTimeUs is long outTimeUs)
            Interlocked.Exchange(ref _firstProgressOutTimeUs, outTimeUs);
        try { FirstFrameObserved?.Invoke(observation); } catch { }
    }

    private void OnBackendNaturalExit(int exitCode, OutputMeta meta)
    {
        _terminalMeta = meta;
        Interlocked.Exchange(ref _exitCode, exitCode);
        Interlocked.Exchange(ref _hasExited, 1);
        _exitSignal.TrySetResult(null);
        if (Volatile.Read(ref _manualStop) == 0)
        {
            try { NaturalExit?.Invoke(exitCode, meta.StderrLog ?? string.Empty); } catch { }
        }
    }

    private static CaptureConfig CreateVideoOnlyConfig(CaptureConfig source, string outputPath)
    {
        return new CaptureConfig
        {
            SourceKind = source.SourceKind,
            Mode = "video",
            Bounds = source.Bounds,
            WindowTitle = source.WindowTitle,
            WindowHandle = source.WindowHandle,
            WindowProcessId = source.WindowProcessId,
            WindowSurfaceBounds = source.WindowSurfaceBounds,
            RequireWindowSurface = true,
            WritePaths = source.WritePaths,
            StorageSafety = source.StorageSafety,
            StorageIntermediatePublication = true,
            AudioSourceKind = AudioCaptureSourceKind.None,
            Microphone = false,
            Fps = source.Fps,
            Quality = source.Quality,
            DurationSeconds = source.DurationSeconds,
            CountdownSeconds = source.CountdownSeconds,
            OutputPath = outputPath,
            OutputConflictPolicy = "overwrite"
        };
    }
}
