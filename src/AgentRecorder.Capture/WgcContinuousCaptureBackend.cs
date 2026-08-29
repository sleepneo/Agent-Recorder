using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Capture;

/// <summary>
/// ICaptureBackend adapter for the WGC continuous recording session managed by
/// <see cref="WgcContinuousManagedSession"/>.
/// </summary>
public sealed class WgcContinuousCaptureBackend : ICaptureBackend, IFirstFrameObservableCaptureBackend, IDeferredCaptureStartBackend
{
    private readonly Func<WgcContinuousSessionOptions, IWgcContinuousBackendSession> _sessionFactory;
    private readonly IStagingToFinalPublisher _publisher;
    private readonly Func<string, OutputMeta> _probe;
    private readonly Func<string> _helperPathResolver;
    private readonly string _tempRoot;

    private readonly object _lifecycleLock = new();
    private readonly TaskCompletionSource<object?> _publishSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _completionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _terminalSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<object?> _stagingReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _finalizationCts = new();
    private readonly IFileCommitGate _commitGate = new FileCommitGate();

    private CaptureConfig? _cfg;
    private IWgcContinuousBackendSession? _session;
    private string? _stagingDir;
    private string? _stagingOutputPath;
    private OutputMeta? _finalMeta;
    private int _exitCode = -1;
    private int _firstFrameFired;
    private int _sessionDisposed;

    // Single atomic arbiter for the natural-exit notification.
    private volatile int _notificationState;
    private readonly TaskCompletionSource<object?> _callbackDispatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _naturalExitCallbackCount;

    internal Action? OnCompletingForTests;
    internal Action? OnDisposeStartingWaitForTests;
    internal Action? OnDisposeCompletingWaitForTests;
    internal Action? OnDisposeAfterCompletingCasForTests;
    internal Action? OnDisposeDisposingWaitForTests;

    /// <summary>
    /// Test seam: invoked after the completion owner has finished publish/probe
    /// cleanup but before it attempts to claim the single atomic notification
    /// arbiter. Used to deterministically test Dispose winning the arbiter.
    /// </summary>
    internal Action? OnBeforeCallbackArbiterForTests;

    /// <summary>
    /// Test seam: grace window allowed for a normal completion before Dispose closes the commit gate.
    /// </summary>
    internal TimeSpan DisposeGraceTimeoutForTests = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Test seam: drain window after cancellation before Dispose returns.
    /// </summary>
    internal TimeSpan DisposeDrainTimeoutForTests = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Test seam: read-only view of the current lifecycle state name.
    /// </summary>
    internal string LifecycleStateNameForTests => ((LifecycleState)_lifecycleState).ToString();

    /// <summary>
    /// Test seam: read-only view of the current notification arbiter state name.
    /// </summary>
    internal string NotificationStateNameForTests => ((NotificationState)_notificationState).ToString();

    /// <summary>
    /// Test seam: number of times the natural-exit callback has actually been dispatched (0 or 1).
    /// </summary>
    internal int NaturalExitCallbackCountForTests => _naturalExitCallbackCount;

    /// <summary>
    /// Test seam: invoked inside <see cref="FireNaturalExit"/> after the
    /// dispatch count has been incremented but before the natural-exit callback
    /// handler is invoked.
    /// </summary>
    internal Action? OnFireNaturalExitForTests;

    /// <summary>
    /// Test seam: invoked after the single atomic arbiter has been claimed by
    /// the natural-exit callback but before the dispatch-started signal is set.
    /// Used to verify that Dispose waits for the dispatch-start evidence without
    /// relying on a fixed timeout.
    /// </summary>
    internal Action? OnCallbackClaimedForTests;

    private Task? _authorizeTask;
    private Action<int, OutputMeta>? _onNaturalExit;
    private bool _deferCaptureStart;
    private int _captureStartRequested;

    /// <summary>
    /// Single atomic arbiter for the natural-exit notification.
    /// Only one winner can be chosen between Dispose and natural completion.
    /// </summary>
    private enum NotificationState
    {
        Open = 0,
        CallbackClaimed = 1,
        DisposeClaimed = 2
    }

    /// <summary>
    /// Lifecycle states used to make Start/Dispose/Completion transitions explicit
    /// and exception-safe.
    /// </summary>
    private enum LifecycleState
    {
        Created = 0,
        Starting = 1,
        Running = 2,
        Completing = 3,
        Completed = 4,
        Disposing = 5,
        Disposed = 6
    }

    private volatile int _lifecycleState;

    /// <summary>
    /// Production constructor using real helper resolution, managed session,
    /// atomic publisher and FFmpeg-based media probing.
    /// </summary>
    public WgcContinuousCaptureBackend()
        : this(
            options => new WgcContinuousManagedSession(options),
            StagingToFinalPublisher.Instance,
            FfmpegCaptureBackend.Probe,
            WgcHelperExePathResolver.Resolve,
            GetDefaultTempRoot())
    {
    }

    /// <summary>
    /// Test-only constructor: inject all seams.
    /// </summary>
    internal WgcContinuousCaptureBackend(
        Func<WgcContinuousSessionOptions, IWgcContinuousBackendSession> sessionFactory,
        IStagingToFinalPublisher publisher,
        Func<string, OutputMeta> probe,
        Func<string> helperPathResolver,
        string tempRoot)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _helperPathResolver = helperPathResolver ?? throw new ArgumentNullException(nameof(helperPathResolver));
        _tempRoot = tempRoot ?? throw new ArgumentNullException(nameof(tempRoot));
    }

    /// <inheritdoc />
    public event Action<FirstFrameObservation>? FirstFrameObserved;

    /// <inheritdoc />
    public event Action<bool>? CaptureAuthorizationCompleted;

    /// <inheritdoc />
    public bool IsAwaitingCaptureStart
    {
        get
        {
            return _deferCaptureStart &&
                   _captureStartRequested == 0 &&
                   _lifecycleState == (int)LifecycleState.Running;
        }
    }

    /// <inheritdoc />
    public void StartCapture()
    {
        // Exactly-once authorization gate: duplicate countdown completion,
        // stop, natural exit, or shutdown races must never authorize twice.
        if (Interlocked.Exchange(ref _captureStartRequested, 1) != 0)
            return;

        IWgcContinuousBackendSession? session;
        lock (_lifecycleLock) session = _session;

        if (session == null || _lifecycleState != (int)LifecycleState.Running)
        {
            NotifyCaptureAuthorizationCompleted(false);
            return;
        }

        _authorizeTask = AuthorizeSessionWithNotificationAsync(session);
    }

    private async Task AuthorizeSessionWithNotificationAsync(IWgcContinuousBackendSession session)
    {
        bool authorized = false;
        try
        {
            authorized = await session.AuthorizeCapture().ConfigureAwait(false);
        }
        catch
        {
            // Authorization failures are surfaced through the session completion
            // path so that StartCapture never has to block waiting for a result.
            authorized = false;
        }

        NotifyCaptureAuthorizationCompleted(authorized);
    }

    private void NotifyCaptureAuthorizationCompleted(bool authorized)
    {
        try
        {
            CaptureAuthorizationCompleted?.Invoke(authorized);
        }
        catch
        {
            // Observers must not affect the recording flow.
        }
    }

    /// <inheritdoc />
    public int ExitCode
    {
        get
        {
            lock (_lifecycleLock) return _exitCode;
        }
    }

    /// <inheritdoc />
    public void OnNaturalExit(Action<int, OutputMeta> callback)
    {
        if (callback == null)
            throw new ArgumentNullException(nameof(callback));
        lock (_lifecycleLock) _onNaturalExit = callback;
    }

    /// <inheritdoc />
    public void Start(CaptureConfig cfg)
    {
        if (cfg == null)
            throw new ArgumentNullException(nameof(cfg));

        // Keep direct backend callers compatible with the historical
        // Microphone=true field while the product path uses AudioSourceKind.
        cfg.NormalizeAudioSource();
        ValidateConfig(cfg);

        // Atomic reservation: only one Start can leave Created.
        int previous = Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Starting, (int)LifecycleState.Created);
        if (previous == (int)LifecycleState.Disposed || previous == (int)LifecycleState.Completed)
            throw new ObjectDisposedException(nameof(WgcContinuousCaptureBackend));
        if (previous != (int)LifecycleState.Created)
            throw new InvalidOperationException("WGC continuous backend has already been started.");

        IWgcContinuousBackendSession? session = null;
        string? stagingDir = null;
        string? stagingOutput = null;
        string? beginSignal = null;
        string? stopSignal = null;
        string? token = null;
        string? helperExePath = null;
        WgcContinuousSessionOptions? options = null;

        try
        {
            helperExePath = _helperPathResolver();

            stagingDir = CreateStagingDirectory();

            stagingOutput = Path.Combine(stagingDir, "capture.mp4");
            beginSignal = Path.Combine(stagingDir, "begin.signal");
            stopSignal = Path.Combine(stagingDir, "stop.signal");
            token = GenerateBeginToken();

            options = BuildSessionOptions(cfg, helperExePath, stagingOutput, beginSignal, stopSignal, token);
            cfg.CommandArgs = BuildRedactedCommandArgs(options, token);

            session = _sessionFactory(options);

            lock (_lifecycleLock)
            {
                _session = session;
                _cfg = cfg;
                _stagingDir = stagingDir;
                _stagingOutputPath = stagingOutput;
                _deferCaptureStart = cfg.DeferCaptureStart;
            }

            session.FirstFrameObserved += OnSessionFirstFrame;

            // Wire the completion continuation BEFORE any operation that can
            // complete the session, so synchronous completions are always handled.
            _ = session.CompletionTask.ContinueWith(
                OnSessionCompleted,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.RunContinuationsAsynchronously);

            // Publish the Running transition. If Dispose already won, this fails
            // and the catch block performs full cleanup.
            previous = Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Running, (int)LifecycleState.Starting);
            if (previous == (int)LifecycleState.Disposed)
                throw new ObjectDisposedException(nameof(WgcContinuousCaptureBackend));

            // The completion continuation was wired before the Running transition. If
            // the session was already completed (faulted, cancelled, or a test seam),
            // that continuation ran while the state was still Starting and did nothing.
            // Dispatch it once more now that Running has been published.
            if (session.CompletionTask.IsCompleted)
            {
                OnSessionCompleted(session.CompletionTask);
            }

            var startTask = session.StartAsync();
            if (startTask != null)
            {
                if (startTask.IsCompleted && !startTask.IsCompletedSuccessfully)
                {
                    var startEx = startTask.Exception?.GetBaseException() ?? new OperationCanceledException();
                    int snapshot = _lifecycleState;
                    if (snapshot == (int)LifecycleState.Disposed || snapshot == (int)LifecycleState.Disposing)
                    {
                        // Dispose won the race; preserve the disposed terminal state
                        // but still publish a deterministic failure reason.
                        var faultedMeta = BuildFailureMetaFromException(startEx, cfg);
                        lock (_lifecycleLock)
                        {
                            if (_finalMeta == null)
                                _finalMeta = faultedMeta;
                        }
                        SignalPublish();
                        _stagingReleased.TrySetResult(null);
                        _terminalSignal.TrySetResult(null);
                        _completionSignal.TrySetResult(null);
                    }
                    else
                    {
                        throw startEx;
                    }
                }
                else if (!startTask.IsCompleted)
                {
                    // Avoid unobserved-task exceptions from fire-and-forget StartAsync tasks.
                    _ = ObserveStartTaskAsync(startTask);
                }
            }

            _authorizeTask = _deferCaptureStart
                ? null
                : AuthorizeSessionAsync(session);
        }
        catch (Exception ex)
        {
            // Ensure we do not leave a half-initialized backend behind.
            RollbackStart(session, stagingDir, cfg, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public OutputMeta Stop()
    {
        IWgcContinuousBackendSession? session;
        Task? authorizeTask;
        LifecycleState state;

        lock (_lifecycleLock)
        {
            state = (LifecycleState)_lifecycleState;
            if (state == LifecycleState.Disposed)
                return _finalMeta ?? new OutputMeta();
            if (_finalMeta != null)
                return _finalMeta;
            session = _session;
            authorizeTask = _authorizeTask;
        }

        if (state == LifecycleState.Created)
        {
            // Stop before Start: nothing to do, but still need a deterministic result.
            var empty = new OutputMeta();
            lock (_lifecycleLock) _finalMeta = empty;
            SignalPublish();
            return empty;
        }

        // Do not request stop while authorization is still writing the begin
        // signal; otherwise the session finalization cancels the authorization
        // task before the helper ever receives the token.
        if (authorizeTask != null)
        {
            try
            {
                authorizeTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best effort: proceed to request stop even if authorization
                // faulted or timed out.
            }
        }

        if (session != null)
        {
            try
            {
                _ = session.RequestStop().GetAwaiter().GetResult();
            }
            catch (ObjectDisposedException)
            {
                // Dispose won the race; the completion handler still produces a result.
            }
            catch
            {
                // Best effort: wait for the completion handler to finish below.
            }
        }

        WaitForPublish(TimeSpan.FromSeconds(15));
        return _finalMeta ?? new OutputMeta();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        int current = _lifecycleState;

        while (true)
        {
            switch ((LifecycleState)current)
            {
                case LifecycleState.Disposed:
                    return;

                case LifecycleState.Completed:
                    // Every Dispose path must participate in notification arbitration before
                    // returning. For Completed this is a no-op wait (callback dispatch already
                    // happened), but it keeps the invariant uniform.
                    ClaimDisposeNotification();

                    // A naturally completed session still needs to be disposed exactly once.
                    if (Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Disposed, (int)LifecycleState.Completed) == (int)LifecycleState.Completed)
                    {
                        DisposeSession();
                        CleanupStaging();
                        SignalPublish();
                        ReleaseStagingIfCleaned();
                        _terminalSignal.TrySetResult(null);
                        _completionSignal.TrySetResult(null);
                    }
                    return;

                case LifecycleState.Created:
                    // No callback can exist yet, but claim the arbiter so a late Start
                    // rollback cannot accidentally dispatch one.
                    ClaimDisposeNotification();

                    if (Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Disposed, (int)LifecycleState.Created) == (int)LifecycleState.Created)
                    {
                        _finalMeta = new OutputMeta();
                        SignalPublish();
                        _terminalSignal.TrySetResult(null);
                        _completionSignal.TrySetResult(null);
                        return;
                    }
                    current = _lifecycleState;
                    continue;

                case LifecycleState.Starting:
                    // Claim the notification arbiter before the lifecycle CAS so a concurrent
                    // Start rollback can never dispatch a natural-exit callback.
                    ClaimDisposeNotification();

                    if (Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Disposed, (int)LifecycleState.Starting) == (int)LifecycleState.Starting)
                    {
                        // Start will see Disposed and rollback, which signals _terminalSignal.
                        OnDisposeStartingWaitForTests?.Invoke();
                        WaitForTerminal(DisposeGraceTimeoutForTests);
                        return;
                    }
                    current = _lifecycleState;
                    continue;

                case LifecycleState.Running:
                    if (Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Disposing, (int)LifecycleState.Running) == (int)LifecycleState.Running)
                    {
                        // Claim the notification arbiter. If the callback already won,
                        // wait until it has started dispatching before proceeding.
                        ClaimDisposeNotification();

                        // Request the session to complete so the completion handler can run.
                        DisposeSession();

                        // Two-phase Dispose: grace window for a normal completion, then cancel
                        // the finalization token and wait a short drain before returning.
                        WaitForTerminal(DisposeGraceTimeoutForTests);
                        if (!_terminalSignal.Task.IsCompleted)
                        {
                            CancelFinalization();
                            _commitGate.Close();
                            WaitForTerminal(DisposeDrainTimeoutForTests);
                        }

                        CleanupStaging();
                        Interlocked.Exchange(ref _lifecycleState, (int)LifecycleState.Disposed);
                        ReleaseStagingIfCleaned();
                        _terminalSignal.TrySetResult(null);
                        _completionSignal.TrySetResult(null);
                        return;
                    }
                    current = _lifecycleState;
                    continue;

                case LifecycleState.Completing:
                    // Inform tests that Dispose has observed the Completing state
                    // and is about to wait for the completion owner to release staging.
                    OnDisposeCompletingWaitForTests?.Invoke();

                    // Claim lifecycle ownership.
                    if (Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Disposing, (int)LifecycleState.Completing) != (int)LifecycleState.Completing)
                    {
                        current = _lifecycleState;
                        continue;
                    }

                    // Test seam: pause after the lifecycle CAS but before the
                    // single atomic notification arbitration, so tests can verify
                    // that concurrent Dispose/callback racing still yields one winner.
                    OnDisposeAfterCompletingCasForTests?.Invoke();

                    // Single atomic arbiter: if the callback already claimed the
                    // notification token, wait until it has started dispatching.
                    ClaimDisposeNotification();
                    DisposeSession();

                    WaitForTerminal(DisposeGraceTimeoutForTests);
                    if (!_terminalSignal.Task.IsCompleted)
                    {
                        CancelFinalization();
                        _commitGate.Close();
                        WaitForTerminal(DisposeDrainTimeoutForTests);
                    }

                    CleanupStaging();
                    Interlocked.Exchange(ref _lifecycleState, (int)LifecycleState.Disposed);
                    ReleaseStagingIfCleaned();
                    _terminalSignal.TrySetResult(null);
                    _completionSignal.TrySetResult(null);
                    return;

                case LifecycleState.Disposing:
                    // A later Dispose must not return without participating in the
                    // notification arbitration. If the first owner has not yet claimed,
                    // this call closes the arbiter; if the callback already claimed,
                    // we wait until dispatch has started before returning.
                    ClaimDisposeNotification();

                    OnDisposeDisposingWaitForTests?.Invoke();
                    WaitForTerminal(DisposeDrainTimeoutForTests);
                    return;
            }
        }
    }

    /// <summary>
    /// Claims the natural-exit notification token for Dispose. If the callback
    /// already won the single atomic arbiter, waits until dispatch has started
    /// so that Dispose never returns before the callback begins.
    /// </summary>
    private void ClaimDisposeNotification()
    {
        int previous = Interlocked.CompareExchange(
            ref _notificationState,
            (int)NotificationState.DisposeClaimed,
            (int)NotificationState.Open);

        if (previous == (int)NotificationState.CallbackClaimed)
        {
            WaitForCallbackDispatchStarted();
        }
    }

    private void WaitForCallbackDispatchStarted()
    {
        // The callback has already won the single atomic arbiter. We must not
        // return before it has signalled that dispatch has started; otherwise a
        // callback could begin after Dispose returns. The signal is set in an
        // infallible finally block inside FireNaturalExit, so this wait is safe.
        _callbackDispatchStarted.Task.Wait();
    }

    private void DisposeSession()
    {
        if (Interlocked.Exchange(ref _sessionDisposed, 1) != 0)
            return;

        IWgcContinuousBackendSession? session;
        lock (_lifecycleLock) session = _session;

        try
        {
            session?.Dispose();
        }
        catch
        {
            // Best effort.
        }
    }

    private void CancelFinalization()
    {
        try
        {
            _finalizationCts.Cancel();
        }
        catch
        {
            // Already canceled or disposed; ignore.
        }
    }

    private void ReleaseStagingIfCleaned()
    {
        string? dir;
        lock (_lifecycleLock) dir = _stagingDir;

        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            // Staging is either absent or already removed; it is safe to release waiters.
            _stagingReleased.TrySetResult(null);
        }
    }

    private void RollbackStart(
        IWgcContinuousBackendSession? session,
        string? stagingDir,
        CaptureConfig cfg,
        Exception ex)
    {
        // Prevent any natural-exit callback from being dispatched for a session
        // that never reached Running. If the completion handler already claimed
        // the callback arbiter, wait until dispatch has started before we
        // overwrite the terminal meta, so Dispose callers never return before
        // the callback begins.
        ClaimDisposeNotification();

        // Move to a terminal state BEFORE disposing the session. Disposing the
        // session may complete its CompletionTask and dispatch the wired
        // OnSessionCompleted continuation. If the lifecycle is already Completed,
        // that continuation sees a non-Running state and returns without
        // overwriting the failure meta we are about to publish. Never overwrite
        // a Disposed/Disposing transition won by Dispose.
        while (true)
        {
            int snapshot = _lifecycleState;
            if (snapshot == (int)LifecycleState.Disposed || snapshot == (int)LifecycleState.Disposing)
                break;
            if (snapshot == (int)LifecycleState.Completed)
                break;
            if (snapshot == (int)LifecycleState.Completing)
            {
                // The completion handler won the race; wait for it to finish,
                // then publish our rollback meta so Stop() reports the start
                // failure rather than whatever the handler observed.
                WaitForTerminal(TimeSpan.FromSeconds(5));
                Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Completed, (int)LifecycleState.Completing);
                break;
            }
            if (Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Completed, snapshot) == snapshot)
                break;
        }

        if (session != null)
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // Best effort.
            }

            try
            {
                session.CompletionTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best effort.
            }
        }

        CleanupDirectory(stagingDir);

        var failure = BuildFailureMetaFromException(ex, cfg);
        lock (_lifecycleLock)
        {
            _finalMeta = failure;
            _exitCode = -1;
        }

        SignalPublish();
        _stagingReleased.TrySetResult(null);
        _terminalSignal.TrySetResult(null);
        _completionSignal.TrySetResult(null);
    }

    private static OutputMeta BuildFailureMetaFromException(Exception ex, CaptureConfig cfg)
    {
        var meta = new OutputMeta();
        meta.OutputPath = cfg.OutputPath;
        meta.Container = "mp4";
        meta.Codec = "h264";
        meta.AudioStatus = "not_requested";
        meta.OutputFileExists = false;

        string category = ex switch
        {
            ObjectDisposedException => "disposed_during_start",
            ApiException apiEx => $"api_exception_{apiEx.Code}",
            _ => "start_failed: " + ex.GetType().Name
        };

        meta.Warnings = new[] { "wgc_continuous_" + category };
        return meta;
    }

    private static string GetDefaultTempRoot()
    {
        return Path.Combine(
            Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath(),
            "AgentRecorder");
    }

    private static void ValidateConfig(CaptureConfig cfg)
    {
        bool isDisplay = string.Equals(cfg.SourceKind, "display", StringComparison.Ordinal);
        bool isWindow = string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal);
        bool isRegion = string.Equals(cfg.SourceKind, "region", StringComparison.Ordinal);
        if (!isDisplay && !isWindow && !isRegion)
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                $"WGC continuous backend only supports source_kind='display', 'window', or 'region' (got '{cfg.SourceKind}').");
        }

        var (_, _, w, h) = cfg.Bounds;
        if (w <= 0 || h <= 0)
        {
            throw new ApiException(400, "INVALID_ARGUMENT", isWindow
                ? "WGC continuous window backend requires positive target bounds."
                : "WGC continuous backend requires positive capture width and height.");
        }

        if (isRegion)
        {
            if (string.IsNullOrWhiteSpace(cfg.DisplayId) || !cfg.DisplayBounds.HasValue)
                throw new ApiException(400, "INVALID_ARGUMENT",
                    "WGC continuous region backend requires a target display identity and complete display bounds.");

            var display = cfg.DisplayBounds.Value;
            if (!WgcRegionGeometry.TryGetCrop(
                    new WgcRegionRect(display.x, display.y, display.w, display.h),
                    new WgcRegionRect(cfg.Bounds.x, cfg.Bounds.y, cfg.Bounds.w, cfg.Bounds.h),
                    out _, out _))
                throw new ApiException(400, "INVALID_ARGUMENT",
                    "WGC continuous region backend requires an even region contained within display bounds.");
        }

        if (isWindow && cfg.WindowHandle == nint.Zero)
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "WGC continuous window backend requires a non-zero HWND.");
        }

        if (!cfg.DurationSeconds.HasValue)
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "WGC continuous backend requires DurationSeconds.");
        }

        int duration = cfg.DurationSeconds.Value;
        if (!WgcContinuousDurationPolicy.IsEligibleSeconds(duration))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                $"WGC continuous backend DurationSeconds must be between {WgcContinuousDurationPolicy.MinSeconds} and {WgcContinuousDurationPolicy.MaxSeconds}.");
        }

        if (cfg.Fps < 1 || cfg.Fps > 60)
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "WGC continuous backend Fps must be between 1 and 60.");
        }

        if (cfg.AudioRequested)
        {
            string audioSource = cfg.IsMicrophone
                ? "microphone"
                : cfg.IsSystemLoopback ? "system audio" : "audio";
            throw new ApiException(400, "UNSUPPORTED_FEATURE",
                $"WGC continuous backend does not support {audioSource} capture.");
        }

        if (string.IsNullOrWhiteSpace(cfg.OutputPath))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "WGC continuous backend requires an output path.");
        }

        if (!Path.IsPathRooted(cfg.OutputPath))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "WGC continuous backend requires an absolute output path.");
        }

        if (!string.Equals(Path.GetExtension(cfg.OutputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(400, "INVALID_ARGUMENT",
                "WGC continuous backend output path must have .mp4 extension.");
        }
    }

    private string CreateStagingDirectory()
    {
        try
        {
            string dir = Path.Combine(_tempRoot, "wgc-continuous", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (Exception ex)
        {
            throw new ApiException(500, "ENCODER_ERROR",
                "Failed to create WGC continuous staging directory: " + ex.Message);
        }
    }

    private static WgcContinuousSessionOptions BuildSessionOptions(
        CaptureConfig cfg,
        string helperExePath,
        string stagingOutput,
        string beginSignal,
        string stopSignal,
        string token)
    {
        int durationMs = WgcContinuousDurationPolicy.ToMilliseconds(cfg.DurationSeconds!.Value);
        return new WgcContinuousSessionOptions
        {
            HelperExePath = helperExePath,
            RecordingId = "wgc-c-" + Guid.NewGuid().ToString("N")[..16],
            TargetKind = string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal)
                ? WgcContinuousTargetKind.Window
                : string.Equals(cfg.SourceKind, "region", StringComparison.Ordinal)
                    ? WgcContinuousTargetKind.Region
                    : WgcContinuousTargetKind.Display,
            DisplayX = cfg.DisplayBounds?.x ?? cfg.Bounds.x,
            DisplayY = cfg.DisplayBounds?.y ?? cfg.Bounds.y,
            DisplayWidth = cfg.DisplayBounds?.w ?? cfg.Bounds.w,
            DisplayHeight = cfg.DisplayBounds?.h ?? cfg.Bounds.h,
            RegionX = cfg.Bounds.x,
            RegionY = cfg.Bounds.y,
            RegionWidth = cfg.Bounds.w,
            RegionHeight = cfg.Bounds.h,
            WindowHandle = cfg.WindowHandle,
            OutputPath = stagingOutput,
            DurationMs = durationMs,
            Fps = cfg.Fps,
            EncoderMode = WgcEncoderModePolicy.NormalizeEnvironment(),
            BeginSignalPath = beginSignal,
            BeginToken = token,
            BeginTimeoutMs = 30000,
            StopSignalPath = stopSignal,
            ProcessTimeoutMs = Math.Max(30000, durationMs + 15000),
            StopWaitTimeoutMs = 10000
        };
    }

    private static string BuildRedactedCommandArgs(WgcContinuousSessionOptions options, string token)
    {
        var args = new List<string>
        {
            options.TargetKind == WgcContinuousTargetKind.Window
                ? "--capture-continuous-window"
                : options.TargetKind == WgcContinuousTargetKind.Region
                    ? "--capture-continuous-region"
                    : "--capture-continuous-display"
        };

        if (options.TargetKind == WgcContinuousTargetKind.Window)
        {
            args.Add("--window-hwnd");
            args.Add($"0x{unchecked((ulong)options.WindowHandle.ToInt64()):X}");
        }
        else if (options.TargetKind == WgcContinuousTargetKind.Region)
        {
            args.Add("--display-bounds");
            args.Add(FormattableString.Invariant($"{options.DisplayX},{options.DisplayY},{options.DisplayWidth},{options.DisplayHeight}"));
            args.Add("--region-bounds");
            args.Add(FormattableString.Invariant($"{options.RegionX},{options.RegionY},{options.RegionWidth},{options.RegionHeight}"));
        }
        else
        {
            args.Add("--display-bounds");
            args.Add(FormattableString.Invariant($"{options.DisplayX},{options.DisplayY},{options.DisplayWidth},{options.DisplayHeight}"));
        }

        args.AddRange(new[]
        {
            "--recording-id",
            options.RecordingId,
            "--output",
            options.OutputPath,
            "--duration-ms",
            options.DurationMs.ToString(CultureInfo.InvariantCulture),
            "--fps",
            options.Fps.ToString(CultureInfo.InvariantCulture),
            "--encoder-mode",
            WgcEncoderModePolicy.ToArgumentValue(options.EncoderMode),
            "--begin-signal",
            options.BeginSignalPath,
            "--begin-token",
            options.BeginToken,
            "--begin-timeout-ms",
            options.BeginTimeoutMs.ToString(CultureInfo.InvariantCulture),
            "--stop-signal",
            options.StopSignalPath,
            "--i-understand-this-captures-screen"
        });

        string rendered = FfmpegCaptureBackend.RenderCommandArgs(args);
        return rendered.Replace(token, "<redacted>", StringComparison.Ordinal);
    }

    private static string GenerateBeginToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static async Task AuthorizeSessionAsync(IWgcContinuousBackendSession session)
    {
        try
        {
            await session.AuthorizeCapture().ConfigureAwait(false);
        }
        catch
        {
            // Authorization failures are surfaced through the session completion
            // path so that Start() never has to block waiting for a result.
        }
    }

    private static async Task ObserveStartTaskAsync(Task startTask)
    {
        try
        {
            await startTask.ConfigureAwait(false);
        }
        catch
        {
            // Exceptions from StartAsync that were not immediately observed are
            // surfaced through the session completion path; swallowing here only
            // prevents unobserved-task exceptions from tearing down the process.
        }
    }

    private void OnSessionFirstFrame(FirstFrameObservation observation)
    {
        if (Interlocked.Exchange(ref _firstFrameFired, 1) != 0)
            return;

        try
        {
            FirstFrameObserved?.Invoke(observation);
        }
        catch
        {
            // Observers must not affect the recording flow.
        }
    }

    private void OnSessionCompleted(Task<WgcContinuousSessionResult> task)
    {
        WgcContinuousSessionResult result;
        try
        {
            result = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : new WgcContinuousSessionResult
                {
                    State = WgcContinuousManagedSessionState.Failed,
                    FailurePhase = "lifecycle",
                    FailureCategory = "session_task_faulted",
                    ExitCode = -1
                };
        }
        catch
        {
            result = new WgcContinuousSessionResult
            {
                State = WgcContinuousManagedSessionState.Failed,
                FailurePhase = "lifecycle",
                FailureCategory = "session_task_faulted",
                ExitCode = -1
            };
        }

        ProcessResult(result);
    }

    private void ProcessResult(WgcContinuousSessionResult result)
    {
        CaptureConfig? cfg;
        string? stagingOutput;
        lock (_lifecycleLock)
        {
            cfg = _cfg;
            stagingOutput = _stagingOutputPath;
        }

        int previous = Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Completing, (int)LifecycleState.Running);
        if (previous == (int)LifecycleState.Disposed || previous == (int)LifecycleState.Disposing)
        {
            // Dispose is responsible for cleanup; do not publish or fire callbacks.
            // Still record a deterministic failure meta so callers receive a reason.
            var disposedMeta = BuildFailureMeta(result, cfg);
            lock (_lifecycleLock)
            {
                if (_finalMeta == null)
                    _finalMeta = disposedMeta;
            }
            SignalPublish();
            _stagingReleased.TrySetResult(null);
            _terminalSignal.TrySetResult(null);
            return;
        }

        if (previous != (int)LifecycleState.Running)
        {
            // Already processed or never reached Running; ignore.
            return;
        }

        // Inform tests that we have claimed the completion-owner role and are
        // about to perform probe/publish work while holding the Completing state.
        OutputMeta meta;
        bool shouldPublish = result.State is WgcContinuousManagedSessionState.Success
            or WgcContinuousManagedSessionState.Stopped;

        try
        {
            OnCompletingForTests?.Invoke();

            if (shouldPublish && cfg != null && !string.IsNullOrEmpty(stagingOutput))
            {
                meta = TryPublishAndProbe(result, cfg, stagingOutput);
            }
            else
            {
                meta = BuildFailureMeta(result, cfg);
            }

            // Preserve the authenticated helper/process exit code even when
            // post-capture probing rejects the bytes. Output validation is a
            // distinct terminal category, not evidence that the process exited
            // abnormally.
            int finalExitCode = result.ExitCode;
            if (!IsSuccessMeta(meta) &&
                finalExitCode == 0 &&
                !string.Equals(meta.StopReason, "output_validation_failed", StringComparison.Ordinal))
            {
                finalExitCode = -1;
            }

            lock (_lifecycleLock)
            {
                _finalMeta = meta;
                _exitCode = finalExitCode;
            }

            SignalPublish();
        }
        catch (Exception ex)
        {
            var failureMeta = BuildFailureMeta(result, cfg, "completion_owner_exception: " + ex.GetType().Name);
            lock (_lifecycleLock)
            {
                if (_finalMeta == null)
                    _finalMeta = failureMeta;
                if (_exitCode == 0)
                    _exitCode = -1;
            }
            SignalPublish();
        }
        finally
        {
            CleanupStaging();
            _stagingReleased.TrySetResult(null);

            bool stoppedByCaller;
            lock (_lifecycleLock)
            {
                stoppedByCaller = result.StopRequestedByCaller;
            }

            if (!stoppedByCaller && !_commitGate.IsClosed)
            {
                // Test seam: pause before claiming the notification arbiter so
                // tests can deterministically verify Dispose winning the race.
                OnBeforeCallbackArbiterForTests?.Invoke();

                // Single atomic arbiter: only the thread that successfully moves
                // the notification state from Open to CallbackClaimed may dispatch
                // the natural-exit callback.
                int previousNotification = Interlocked.CompareExchange(
                    ref _notificationState,
                    (int)NotificationState.CallbackClaimed,
                    (int)NotificationState.Open);

                if (previousNotification == (int)NotificationState.Open)
                {
                    int exitCode;
                    OutputMeta? finalMetaSnapshot;
                    lock (_lifecycleLock)
                    {
                        exitCode = _exitCode;
                        finalMetaSnapshot = _finalMeta;
                    }
                    FireNaturalExit(exitCode, finalMetaSnapshot ?? new OutputMeta());
                }
            }

            // Only publish the Completed transition if Dispose has not already
            // moved us to Disposed/Disposing. This guarantees a late completion
            // owner can never revert Disposed back to Completed.
            Interlocked.CompareExchange(ref _lifecycleState, (int)LifecycleState.Completed, (int)LifecycleState.Completing);
            _terminalSignal.TrySetResult(null);
            _completionSignal.TrySetResult(null);
        }
    }

    private OutputMeta TryPublishAndProbe(WgcContinuousSessionResult result, CaptureConfig cfg, string stagingOutput)
    {
        _finalizationCts.Token.ThrowIfCancellationRequested();

        long stagingSize;
        try
        {
            stagingSize = new FileInfo(stagingOutput).Length;
        }
        catch (Exception ex)
        {
            return BuildFailureMeta(result, cfg, "staging_access_error: " + ex.GetType().Name);
        }

        if (stagingSize <= 0)
        {
            return BuildFailureMeta(result, cfg, "missing_or_empty_staging_file");
        }

        _finalizationCts.Token.ThrowIfCancellationRequested();

        // All authenticity checks run against the staging file BEFORE any commit
        // to the final path, so an existing final file is never overwritten by an
        // invalid or unverified candidate.
        OutputMeta probeMeta;
        try
        {
            probeMeta = _probe(stagingOutput);
        }
        catch (Exception ex)
        {
            return BuildFailureMeta(result, cfg, "probe_exception: " + ex.GetType().Name);
        }

        _finalizationCts.Token.ThrowIfCancellationRequested();

        var validationWarnings = ValidateProbeAndSummary(probeMeta, result.Summary, stagingSize, cfg);
        if (validationWarnings.Count > 0)
        {
            var context = CopyOutputMeta(probeMeta);
            context.Warnings = validationWarnings.ToArray();
            return BuildFailureMeta(result, cfg, "output_validation_failed", context);
        }

        _finalizationCts.Token.ThrowIfCancellationRequested();

        // All pre-commit checks passed; atomically publish the verified staging bytes.
        PublishResult publish;
        try
        {
            publish = _publisher.PublishAsync(stagingOutput, cfg.OutputPath, _finalizationCts.Token, _commitGate)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return BuildFailureMeta(result, cfg, "publish_exception: " + ex.GetType().Name);
        }

        if (!publish.Success)
        {
            return BuildFailureMeta(result, cfg, "publish_failed: " + (publish.FailureCategory ?? "unknown"));
        }

        // Commit succeeded. From here on we only perform infallible field assignment.
        long finalSize;
        try
        {
            finalSize = new FileInfo(cfg.OutputPath).Length;
        }
        catch
        {
            finalSize = publish.FinalSizeBytes;
        }

        probeMeta.OutputPath = cfg.OutputPath;
        probeMeta.SizeBytes = finalSize;
        probeMeta.OutputFileExists = true;
        probeMeta.CaptureMethod = result.Summary?.CaptureMethod;
        probeMeta.VideoEncoderMode = result.Summary?.EncoderMode;
        probeMeta.VideoEncoderSelectionReason = result.Summary?.EncoderSelectionReason;
        probeMeta.Stage = result.Summary?.State.ToString();
        probeMeta.AudioStatus = "not_requested";
        probeMeta.StderrLog = result.StderrTail;
        probeMeta.Warnings = Array.Empty<string>();

        return probeMeta;
    }

    private static OutputMeta CopyOutputMeta(OutputMeta source)
    {
        return new OutputMeta
        {
            SizeBytes = source.SizeBytes,
            DurationSeconds = source.DurationSeconds,
            Width = source.Width,
            Height = source.Height,
            Fps = source.Fps,
            StderrLog = source.StderrLog,
            Warnings = source.Warnings,
            OutputPath = source.OutputPath,
            Container = source.Container,
            Codec = source.Codec,
            CaptureMethod = source.CaptureMethod,
            VideoEncoderMode = source.VideoEncoderMode,
            VideoEncoderSelectionReason = source.VideoEncoderSelectionReason,
            Stage = source.Stage,
            StopReason = source.StopReason,
            Hresult = source.Hresult,
            OutputFileExists = source.OutputFileExists,
            IsValidPngSignature = source.IsValidPngSignature,
            AudioStatus = source.AudioStatus,
            HasAudioStream = source.HasAudioStream,
            AudioCodec = source.AudioCodec,
            AudioLostAtMs = source.AudioLostAtMs
        };
    }

    private static List<string> ValidateProbeAndSummary(
        OutputMeta probe,
        WgcContinuousSessionSummary? summary,
        long stagingSize,
        CaptureConfig cfg)
    {
        var warnings = new List<string>();
        string expectedCaptureMethod = string.Equals(cfg.SourceKind, "window", StringComparison.Ordinal)
            ? "WGC_D3D11_WINDOW_FRAME_STREAM"
            : string.Equals(cfg.SourceKind, "region", StringComparison.Ordinal)
                ? "WGC_D3D11_REGION_FRAME_STREAM"
                : "WGC_D3D11_FRAME_STREAM";

        // The media probe validates the bytes and usually has no knowledge of
        // which capture source produced them. The authenticated helper STARTED
        // event is the source-of-truth for the target-specific method.
        if (!string.IsNullOrEmpty(probe.CaptureMethod) &&
            !string.Equals(probe.CaptureMethod, expectedCaptureMethod, StringComparison.Ordinal))
        {
            warnings.Add($"capture_method_mismatch: expected={expectedCaptureMethod} actual={probe.CaptureMethod}");
        }

        if (probe.SizeBytes != stagingSize)
        {
            warnings.Add($"probe_size_mismatch: probe={probe.SizeBytes} staging={stagingSize}");
        }

        if (!string.Equals(probe.Container, "mp4", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"unexpected_container: {probe.Container}");
        }

        if (!string.Equals(probe.Codec, "h264", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"unexpected_codec: {probe.Codec}");
        }

        if (probe.Width <= 0 || probe.Height <= 0)
        {
            warnings.Add($"invalid_dimensions: width={probe.Width} height={probe.Height}");
        }

        if (string.Equals(cfg.SourceKind, "region", StringComparison.Ordinal))
        {
            if (probe.Width != cfg.Bounds.w || probe.Height != cfg.Bounds.h)
                warnings.Add($"region_dimensions_mismatch: expected={cfg.Bounds.w}x{cfg.Bounds.h} actual={probe.Width}x{probe.Height}");
        }

        if (probe.DurationSeconds <= 0)
        {
            warnings.Add($"invalid_duration: {probe.DurationSeconds}");
        }

        if (stagingSize < 512)
        {
            warnings.Add($"staging_too_small: {stagingSize}");
        }

        if (summary != null)
        {
            if (!string.Equals(summary.CaptureMethod, expectedCaptureMethod, StringComparison.Ordinal))
            {
                warnings.Add($"summary_capture_method_mismatch: expected={expectedCaptureMethod} actual={summary.CaptureMethod}");
            }
            if (!string.Equals(summary.EncoderMode, "software", StringComparison.Ordinal) &&
                !string.Equals(summary.EncoderMode, "hardware", StringComparison.Ordinal))
            {
                warnings.Add($"invalid_encoder_mode: {summary.EncoderMode}");
            }
            if (string.IsNullOrEmpty(summary.EncoderSelectionReason))
            {
                warnings.Add("missing_encoder_selection_reason");
            }
            if (summary.Width.HasValue && probe.Width > 0 && summary.Width.Value != probe.Width)
            {
                warnings.Add($"width_mismatch: probe={probe.Width} summary={summary.Width.Value}");
            }

            if (summary.Height.HasValue && probe.Height > 0 && summary.Height.Value != probe.Height)
            {
                warnings.Add($"height_mismatch: probe={probe.Height} summary={summary.Height.Value}");
            }

            if (summary.HasFileSize && summary.FileSize.HasValue && summary.FileSize.Value != stagingSize)
            {
                warnings.Add($"summary_size_mismatch: summary={summary.FileSize.Value} staging={stagingSize}");
            }

            if (summary.DurationMs.HasValue && probe.DurationSeconds > 0)
            {
                long probeDurationMs = (long)(probe.DurationSeconds * 1000);
                long summaryDurationMs = summary.DurationMs.Value;
                if (Math.Abs(probeDurationMs - summaryDurationMs) > 200)
                {
                    warnings.Add($"duration_mismatch: probe={probeDurationMs}ms summary={summaryDurationMs}ms");
                }
            }
        }

        return warnings;
    }

    private static OutputMeta BuildFailureMeta(
        WgcContinuousSessionResult result,
        CaptureConfig? cfg,
        string? extraCategory = null,
        OutputMeta? context = null)
    {
        var meta = context ?? new OutputMeta();
        meta.OutputPath = cfg?.OutputPath;
        meta.Container = "mp4";
        meta.Codec = "h264";
        meta.CaptureMethod = result.Summary?.CaptureMethod;
        meta.VideoEncoderMode = result.Summary?.EncoderMode;
        meta.VideoEncoderSelectionReason = result.Summary?.EncoderSelectionReason;
        meta.Stage = result.Summary?.State.ToString();
        meta.AudioStatus = "not_requested";
        meta.StderrLog = result.StderrTail;
        meta.OutputFileExists = false;

        var warnings = new List<string>();
        if (context?.Warnings != null)
        {
            warnings.AddRange(context.Warnings);
        }

        bool authenticatedTerminal = result.State is
            WgcContinuousManagedSessionState.Success or
            WgcContinuousManagedSessionState.Stopped;
        string category;

        if (authenticatedTerminal && !string.IsNullOrEmpty(extraCategory))
        {
            // A helper that authenticated Success/Stopped did not terminate
            // unexpectedly; the post-capture stage that rejected its bytes is
            // the primary terminal category.
            category = extraCategory;
        }
        else if (result.State == WgcContinuousManagedSessionState.Cancelled)
        {
            category = "cancelled";
        }
        else if (!string.IsNullOrEmpty(result.FailureCategory))
        {
            category = result.FailureCategory;
        }
        else if (result.State == WgcContinuousManagedSessionState.Failed)
        {
            category = "helper_or_session_failed";
        }
        else
        {
            category = "unexpected_terminal_state";
        }

        // Keep the exact helper terminal category as structured metadata. The
        // warning prefix remains diagnostic-only; RecordingEngine uses this
        // field for the public stop_reason/error contract.
        meta.StopReason = result.State is WgcContinuousManagedSessionState.Success
            or WgcContinuousManagedSessionState.Stopped
            ? (extraCategory ?? category)
            : category;

        warnings.Add($"wgc_continuous_{category}");
        if (!string.IsNullOrEmpty(extraCategory) &&
            !string.Equals(category, extraCategory, StringComparison.Ordinal))
        {
            warnings.Add($"wgc_continuous_{extraCategory}");
        }

        if (result.Summary?.PartialOutputExists == true)
        {
            warnings.Add("wgc_continuous_partial_output_not_published");
        }

        meta.Warnings = warnings.ToArray();
        return meta;
    }

    private static bool IsSuccessMeta(OutputMeta meta)
    {
        return meta.OutputFileExists && meta.SizeBytes > 512;
    }

    private void FireNaturalExit(int exitCode, OutputMeta meta)
    {
        // This method is only called after the caller has won the single atomic
        // notification arbiter (CallbackClaimed). The arbitration token itself
        // guarantees Dispose cannot also win, so no additional suppression check
        // is required here.

        // The path from claiming the arbiter to setting the dispatch-started
        // signal must be infallible. Tests may pause on OnCallbackClaimedForTests,
        // but once that returns we atomically publish dispatch-started, the count,
        // and the handler. A finally block guarantees the signal is set even if
        // the test seam throws, preventing Dispose from waiting forever.
        try
        {
            OnCallbackClaimedForTests?.Invoke();
        }
        finally
        {
            // Signal that callback dispatch has started and record the actual
            // dispatch count before invoking the handler, so any Dispose that lost
            // the arbitration can wait until dispatch has begun.
            _callbackDispatchStarted.TrySetResult(null);
            Interlocked.Increment(ref _naturalExitCallbackCount);

            try
            {
                OnFireNaturalExitForTests?.Invoke();
            }
            catch
            {
                // Test seam exceptions must not prevent the handler from running.
            }

            Action<int, OutputMeta>? cb;
            lock (_lifecycleLock) cb = _onNaturalExit;

            try
            {
                cb?.Invoke(exitCode, meta);
            }
            catch
            {
                // Callback handler exceptions must not corrupt backend state.
            }
        }
    }

    private void SignalPublish()
    {
        _publishSignal.TrySetResult(null);
    }

    private void WaitForPublish(TimeSpan timeout)
    {
        try
        {
            _publishSignal.Task.Wait(timeout);
        }
        catch
        {
            // Best effort: caller receives whatever meta is available.
        }
    }

    private void WaitForTerminal(TimeSpan timeout)
    {
        try
        {
            _terminalSignal.Task.Wait(timeout);
        }
        catch
        {
            // Best effort.
        }
    }

    private void WaitForStagingReleased(TimeSpan timeout)
    {
        try
        {
            _stagingReleased.Task.Wait(timeout);
        }
        catch
        {
            // Best effort.
        }
    }

    private void CleanupStaging()
    {
        string? dir;
        lock (_lifecycleLock) dir = _stagingDir;
        CleanupDirectory(dir);

        // Remove the shared wgc-continuous container if it is now empty so
        // staging does not leave an empty parent directory behind.
        if (!string.IsNullOrEmpty(dir))
        {
            string? parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                try
                {
                    if (Directory.GetFiles(parent).Length == 0 &&
                        Directory.GetDirectories(parent).Length == 0)
                    {
                        Directory.Delete(parent, recursive: false);
                    }
                }
                catch
                {
                    // Best effort; other concurrent sessions may own the parent.
                }
            }
        }
    }

    private static void CleanupDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return;

        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Cleanup failures are bounded warnings; they do not override the result.
        }

        // Also remove the empty parent container (e.g., "wgc-continuous") so that
        // rollbacks do not leave an empty directory behind.
        string? parent = Path.GetDirectoryName(dir);
        if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
        {
            try
            {
                if (Directory.GetFiles(parent).Length == 0 &&
                    Directory.GetDirectories(parent).Length == 0)
                {
                    Directory.Delete(parent, recursive: false);
                }
            }
            catch
            {
                // Best effort; other concurrent sessions may own the parent.
            }
        }
    }
}
