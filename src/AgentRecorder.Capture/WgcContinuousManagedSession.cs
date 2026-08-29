using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentRecorder.Capture;

/// <summary>
/// Managed asynchronous session for WGC continuous recording. Responsible for
/// launching the native helper, enforcing the two-phase consent gate, parsing
/// the IPC v2 event stream, reporting first-frame evidence, and cleaning up
/// the process tree and control files on success/failure/cancellation.
///
/// <para>
/// This class is intended to be composed by a future <see cref="ICaptureBackend"/>
/// adapter; it does not itself change public API behavior or default backends.
/// </para>
/// </summary>
public sealed class WgcContinuousManagedSession : IDisposable, IWgcContinuousBackendSession
{
    // Output/input hard bounds to prevent a misbehaving helper from unbounded
    // memory growth in the supervising process.
    private const int MaxStderrChars = 32768;
    private const int MaxStdoutEvents = 10000;
    private const int MaxLinesPerEventBlock = 1000;
    private const int MaxSingleLineLength = 16384;

    // Fixed-size chunk for stdout/stderr reads. Large enough to amortize
    // syscalls, small enough to bound transient allocation.
    private const int StdoutChunkSize = 4096;

    private static readonly TimeSpan ProcessExitAfterKillTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReaderDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LateProcessKillTimeout = TimeSpan.FromSeconds(5);

    private readonly WgcContinuousSessionOptions _options;
    private readonly IWgcContinuousProcess _process;
    private readonly IAuthorizationSignalWriter _signalWriter;
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly List<WgcContinuousEvent> _events = new();
    private readonly BoundedStringBuilder _stderrBuffer;
    private readonly TaskCompletionSource<WgcContinuousSessionResult> _completionTcs = new();
    private readonly TaskCompletionSource<object?> _finalizeSignal = new();
    private readonly object _lock = new();

    private WgcContinuousManagedSessionState _state = WgcContinuousManagedSessionState.NotStarted;
    private bool _started;
    private bool _authorized;
    private Task<bool>? _authorizationTask;
    private bool _stopRequested;
    private bool _completed;
    private Task? _stdoutReader;
    private Task? _stderrReader;
    private Task? _watcher;
    private int _firstFrameObserved;
    private FirstFrameObservation? _firstFrameObservation;
    private bool _seenTerminalEvent;
    private int _exitCode = -1;
    private string? _failureReason;
    private string? _protocolViolation;
    private CancellationTokenRegistration? _externalCancellationRegistration;

    // Tracks linked CancellationTokenSource instances created by authorization
    // attempts. Exposed only for deterministic resource-leak testing.
    private int _activeAuthorizationLinkedCtsCount;
    internal int ActiveAuthorizationLinkedCtsCountForTests => _activeAuthorizationLinkedCtsCount;

    // Test-only seams. Never used in production code paths.
    internal Action? AuthorizeOwnerReservedBarrierForTests;
    internal bool ThrowFromBuildResultForTests { get; set; }

    /// <summary>
    /// Raised for every parsed event. Handlers must not throw.
    /// </summary>
    public event Action<WgcContinuousEvent>? EventReceived;

    /// <summary>
    /// Raised exactly once when credible first-frame evidence is observed:
    /// either the explicit FIRST_FRAME event (preferred, source-frame evidence)
    /// or, as a bounded compatibility fallback for older helpers, a PROGRESS
    /// event with FramesCaptured &gt; 0.
    /// </summary>
    public event Action<FirstFrameObservation>? FirstFrameObserved;

    /// <summary>
    /// Production constructor: resolves the helper executable automatically.
    /// </summary>
    public WgcContinuousManagedSession(WgcContinuousSessionOptions options)
        : this(options, new RealWgcContinuousProcess(), new FileAuthorizationSignalWriter())
    {
    }

    /// <summary>
    /// Test constructor: inject a fake <see cref="IWgcContinuousProcess"/>.
    /// </summary>
    internal WgcContinuousManagedSession(WgcContinuousSessionOptions options, IWgcContinuousProcess process)
        : this(options, process, new FileAuthorizationSignalWriter())
    {
    }

    /// <summary>
    /// Test constructor: inject both a fake process and a signal writer.
    /// </summary>
    internal WgcContinuousManagedSession(
        WgcContinuousSessionOptions options,
        IWgcContinuousProcess process,
        IAuthorizationSignalWriter signalWriter)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _signalWriter = signalWriter ?? throw new ArgumentNullException(nameof(signalWriter));
        _stderrBuffer = new BoundedStringBuilder(MaxStderrChars);
    }

    /// <summary>Current session state snapshot.</summary>
    public WgcContinuousManagedSessionState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>Task that completes once with the final session result.</summary>
    public Task<WgcContinuousSessionResult> CompletionTask => _completionTcs.Task;

    /// <summary>
    /// True when the session has reached a terminal state and the completion
    /// task has been resolved.
    /// </summary>
    public bool IsCompleted
    {
        get { lock (_lock) return _completed && _completionTcs.Task.IsCompleted; }
    }

    /// <summary>
    /// Validates options, starts the helper process, and begins asynchronous
    /// stdout/stderr readers and the lifecycle watcher. Returns quickly and
    /// does not wait for the recording to finish.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_started)
                throw new InvalidOperationException("Session has already been started.");
            _started = true;
        }

        // Already-cancelled token must not launch a process.
        if (cancellationToken.IsCancellationRequested)
        {
            TryEnterCompletionWithReason(WgcContinuousManagedSessionState.Cancelled, "caller_cancelled");
            _ = Task.Run(() => FinalizeAsync(WgcContinuousManagedSessionState.Cancelled, _failureReason));
            return Task.CompletedTask;
        }

        ValidateOptions();

        // Remove stale signal files so a reused token/path cannot bypass the
        // consent gate of this session. Failure here is fatal.
        if (!CleanupSignalsBeforeStart())
        {
            TryEnterCompletionWithReason(WgcContinuousManagedSessionState.Failed, "pre_start_cleanup_failed");
            _ = Task.Run(() => FinalizeAsync(WgcContinuousManagedSessionState.Failed, _failureReason));
            return Task.CompletedTask;
        }

        var args = BuildArgumentList();

        try
        {
            _process.Start(_options.HelperExePath, args);
        }
        catch
        {
            TryEnterCompletionWithReason(WgcContinuousManagedSessionState.Failed, "process_start_failed");
            _ = Task.Run(() => FinalizeAsync(WgcContinuousManagedSessionState.Failed, _failureReason));
            return Task.CompletedTask;
        }

        // At this point a real OS process exists. We must publish ownership and
        // handle the race where Dispose/cancellation completed while Start was
        // in progress.
        bool alreadyCompleted;
        lock (_lock)
        {
            alreadyCompleted = _completed;
            if (!alreadyCompleted)
            {
                _state = WgcContinuousManagedSessionState.WaitingForAuthorization;
            }
        }

        if (alreadyCompleted)
        {
            // Dispose won the race during Start. The late process must not be
            // left unsupervised.
            TerminateLateProcess();
            return Task.CompletedTask;
        }

        // Register external cancellation only after process ownership is
        // established so the handler cannot finalize a not-yet-started process.
        if (cancellationToken.CanBeCanceled)
        {
            _externalCancellationRegistration = cancellationToken.Register(
                () => _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Cancelled, "caller_cancelled"),
                useSynchronizationContext: false);
        }

        _stdoutReader = Task.Run(RunStdoutReader);
        _stderrReader = Task.Run(RunStderrReader);
        _watcher = Task.Run(RunWatcher);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Atomically writes the configured begin token to the begin signal file,
    /// allowing the helper to pass the consent gate. Returns false if the
    /// session is no longer waiting for authorization or has already completed.
    /// Concurrent calls are serialized and observe the same single result.
    /// </summary>
    public Task<bool> AuthorizeCapture(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool>? ownerTcs = null;
        CancellationTokenSource? linked = null;
        lock (_lock)
        {
            if (!_started || _completed)
                return Task.FromResult(false);
            if (_authorized)
                return Task.FromResult(false);
            if (_state != WgcContinuousManagedSessionState.WaitingForAuthorization &&
                _state != WgcContinuousManagedSessionState.Authorizing)
                return Task.FromResult(false);

            // Only the caller that creates the authorization task owns it and
            // can receive true. Concurrent observers must not share the same
            // boolean result.
            if (_authorizationTask != null)
                return Task.FromResult(false);

            _state = WgcContinuousManagedSessionState.Authorizing;
            ownerTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // Creating the linked token source is not file I/O and is kept
                // inside the short state lock so that Dispose cannot dispose
                // _sessionCts between owner reservation and token creation.
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCts.Token);
            }
            catch (Exception)
            {
                // The caller's token source was disposed or the session
                // finalized concurrently. Complete the owner task and leave
                // the state machine consistent without leaking a registration.
                ownerTcs.TrySetResult(false);
                if (!_completed)
                    _state = WgcContinuousManagedSessionState.WaitingForAuthorization;
                return ownerTcs.Task;
            }

            _authorizationTask = ownerTcs.Task;
        }

        // Deterministic test barrier between owner reservation and attempt start.
        // In production this is always null.
        AuthorizeOwnerReservedBarrierForTests?.Invoke();

        // The actual file I/O is performed outside the session lock so that
        // cancellation, Dispose, and concurrent authorization attempts can
        // always acquire the state lock without waiting for I/O.
        Interlocked.Increment(ref _activeAuthorizationLinkedCtsCount);
        _ = RunAuthorizationOnceAsync(ownerTcs, linked);

        return ownerTcs.Task;
    }

    /// <summary>
    /// Creates the stop signal file and waits for the session to complete.
    /// Returns true only when the helper stopped gracefully in response to the
    /// caller's stop request; false for failure, cancellation, timeout, or
    /// natural success without a stop request.
    /// </summary>
    public async Task<bool> RequestStop(CancellationToken cancellationToken = default)
    {
        bool alreadyCompleted;
        bool started;
        lock (_lock)
        {
            alreadyCompleted = _completed;
            started = _started;
            if (!alreadyCompleted && started)
                _stopRequested = true;
        }

        if (alreadyCompleted)
        {
            var result = await _completionTcs.Task
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            return result.State == WgcContinuousManagedSessionState.Stopped;
        }

        if (!started)
            return false;

        if (!CreateStopSignal())
        {
            _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Failed, "stop_signal_create_failed");
            return false;
        }

        _finalizeSignal.TrySetResult(null);

        // Creating the linked token source can race with Dispose: if _sessionCts
        // is already disposed, treat it as a concurrent finalization and report
        // the resulting state instead of faulting the caller.
        CancellationTokenSource? linked;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token, cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            try
            {
                await _completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best effort */ }

            var disposedResult = await _completionTcs.Task.ConfigureAwait(false);
            return disposedResult.State == WgcContinuousManagedSessionState.Stopped;
        }

        using (linked)
        {
            try
            {
                await _completionTcs.Task
                    .WaitAsync(TimeSpan.FromMilliseconds(_options.StopWaitTimeoutMs), linked.Token)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Failed, "stop_wait_timeout");
                return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Cancelled, "caller_cancelled");
                return false;
            }
            catch (OperationCanceledException)
            {
                // The session CTS was cancelled by another finalization path.
                // Wait for the existing completion so we can report its state.
                try
                {
                    await _completionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* best effort */ }
            }
        }

        var finalResult = await _completionTcs.Task.ConfigureAwait(false);
        return finalResult.State == WgcContinuousManagedSessionState.Stopped;
    }

    /// <summary>
    /// Cancels the session, kills the helper process tree if necessary, and
    /// cleans up control files. The method waits a bounded time for cleanup.
    /// </summary>
    public void Dispose()
    {
        // Dispose is a caller-driven cancellation. Finalize synchronously so
        // the cancelled state reliably wins over the background watcher even
        // when the helper exits quickly or ignores stop signals.
        bool enteredCompletion = false;
        try
        {
            enteredCompletion = TryEnterCompletionWithReason(
                WgcContinuousManagedSessionState.Cancelled, "disposed");
            if (enteredCompletion)
            {
                FinalizeAsync(WgcContinuousManagedSessionState.Cancelled, _failureReason)
                    .Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch { /* best effort */ }

        // If another finalization path already owns completion, wait for it
        // to finish before disposing shared resources.
        if (!enteredCompletion)
        {
            try
            {
                _completionTcs.Task.Wait(TimeSpan.FromSeconds(5));
            }
            catch { /* best effort */ }
        }

        try { _externalCancellationRegistration?.Dispose(); }
        catch { /* best effort */ }
        try { _sessionCts.Dispose(); }
        catch { /* best effort */ }
        try { _process.Dispose(); }
        catch { /* best effort */ }
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.HelperExePath))
            throw new ArgumentException("Helper executable path must be provided.", nameof(_options));
        if (!File.Exists(_options.HelperExePath))
            throw new FileNotFoundException("Helper executable not found.", _options.HelperExePath);

        if (string.IsNullOrWhiteSpace(_options.RecordingId))
            throw new ArgumentException("Recording id must be provided.", nameof(_options));
        if (_options.RecordingId.Length is < 1 or > 64)
            throw new ArgumentException("Recording id must be 1-64 characters.", nameof(_options));
        foreach (var c in _options.RecordingId)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
                throw new ArgumentException($"Recording id contains invalid character: {c}", nameof(_options));
        }

        if (_options.TargetKind == WgcContinuousTargetKind.Display)
        {
            if (_options.DisplayWidth <= 0 || _options.DisplayHeight <= 0)
                throw new ArgumentException("Display width and height must be positive.", nameof(_options));
        }
        else if (_options.TargetKind == WgcContinuousTargetKind.Window)
        {
            if (_options.WindowHandle == nint.Zero)
                throw new ArgumentException("Window handle must be non-zero.", nameof(_options));
        }
        else if (_options.TargetKind == WgcContinuousTargetKind.Region)
        {
            if (!WgcRegionGeometry.TryGetCrop(
                    new WgcRegionRect(_options.DisplayX, _options.DisplayY,
                        _options.DisplayWidth, _options.DisplayHeight),
                    new WgcRegionRect(_options.RegionX, _options.RegionY,
                        _options.RegionWidth, _options.RegionHeight),
                    out _, out _))
                throw new ArgumentException(
                    "Region bounds must be even, at least 32x32, and contained within display bounds.",
                    nameof(_options));
        }
        else
        {
            throw new ArgumentException("Unknown WGC continuous target kind.", nameof(_options));
        }

        if (string.IsNullOrWhiteSpace(_options.OutputPath))
            throw new ArgumentException("Output path must be provided.", nameof(_options));
        if (!Path.IsPathRooted(_options.OutputPath))
            throw new ArgumentException("Output path must be absolute.", nameof(_options));
        if (!string.Equals(Path.GetExtension(_options.OutputPath), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must have .mp4 extension.", nameof(_options));

        if (!WgcContinuousDurationPolicy.IsEligibleMilliseconds(_options.DurationMs))
            throw new ArgumentException(
                $"Duration must be between {WgcContinuousDurationPolicy.MinMilliseconds} and {WgcContinuousDurationPolicy.MaxMilliseconds} ms.",
                nameof(_options));

        if (_options.Fps is < 1 or > 60)
            throw new ArgumentException("Fps must be between 1 and 60.", nameof(_options));

        if (_options.EncoderMode is not WgcEncoderMode.Software and not WgcEncoderMode.HardwarePreferred)
            throw new ArgumentException("Encoder mode must be software or hardware-preferred.", nameof(_options));

        if (string.IsNullOrWhiteSpace(_options.BeginSignalPath))
            throw new ArgumentException("Begin signal path must be provided.", nameof(_options));
        if (!Path.IsPathRooted(_options.BeginSignalPath))
            throw new ArgumentException("Begin signal path must be absolute.", nameof(_options));

        if (string.IsNullOrWhiteSpace(_options.BeginToken))
            throw new ArgumentException("Begin token must be provided.", nameof(_options));

        if (_options.BeginTimeoutMs is < 100 or > 300000)
            throw new ArgumentException("Begin timeout must be between 100 and 300000 ms.", nameof(_options));

        if (string.IsNullOrWhiteSpace(_options.StopSignalPath))
            throw new ArgumentException("Stop signal path must be provided.", nameof(_options));
        if (!Path.IsPathRooted(_options.StopSignalPath))
            throw new ArgumentException("Stop signal path must be absolute.", nameof(_options));

        if (string.Equals(
                Path.GetFullPath(_options.BeginSignalPath),
                Path.GetFullPath(_options.StopSignalPath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Begin and stop signal paths must be different.", nameof(_options));

        if (_options.ProcessTimeoutMs <= 0)
            throw new ArgumentException("Process timeout must be positive.", nameof(_options));
        if (_options.StopWaitTimeoutMs <= 0)
            throw new ArgumentException("Stop wait timeout must be positive.", nameof(_options));
    }

    private List<string> BuildArgumentList()
    {
        var args = new List<string>
        {
            _options.TargetKind == WgcContinuousTargetKind.Window
                ? "--capture-continuous-window"
                : _options.TargetKind == WgcContinuousTargetKind.Region
                    ? "--capture-continuous-region"
                    : "--capture-continuous-display"
        };

        if (_options.TargetKind == WgcContinuousTargetKind.Window)
        {
            args.Add("--window-hwnd");
            args.Add($"0x{unchecked((ulong)_options.WindowHandle.ToInt64()):X}");
        }
        else if (_options.TargetKind == WgcContinuousTargetKind.Region)
        {
            args.Add("--display-bounds");
            args.Add(FormattableString.Invariant($"{_options.DisplayX},{_options.DisplayY},{_options.DisplayWidth},{_options.DisplayHeight}"));
            args.Add("--region-bounds");
            args.Add(FormattableString.Invariant($"{_options.RegionX},{_options.RegionY},{_options.RegionWidth},{_options.RegionHeight}"));
        }
        else
        {
            args.Add("--display-bounds");
            args.Add(FormattableString.Invariant($"{_options.DisplayX},{_options.DisplayY},{_options.DisplayWidth},{_options.DisplayHeight}"));
        }

        args.AddRange(new[]
        {
            "--recording-id",
            _options.RecordingId,
            "--output",
            _options.OutputPath,
            "--duration-ms",
            _options.DurationMs.ToString(CultureInfo.InvariantCulture),
            "--fps",
            _options.Fps.ToString(CultureInfo.InvariantCulture),
            "--encoder-mode",
            WgcEncoderModePolicy.ToArgumentValue(_options.EncoderMode),
            "--begin-signal",
            _options.BeginSignalPath,
            "--begin-token",
            _options.BeginToken,
            "--begin-timeout-ms",
            _options.BeginTimeoutMs.ToString(CultureInfo.InvariantCulture),
            "--stop-signal",
            _options.StopSignalPath,
            "--i-understand-this-captures-screen"
        });
        return args;
    }

    private async Task RunAuthorizationOnceAsync(
        TaskCompletionSource<bool> ownerTcs,
        CancellationTokenSource linkedCts)
    {
        try
        {
            using (linkedCts)
            {
                // Yield after reserving the owner task so a caller that
                // cancels immediately after AuthorizeCapture returns is
                // observed before authorization can publish a begin token.
                // This also keeps the linked CTS lifetime deterministic for
                // repeated-cancel cleanup paths.
                await Task.Yield();
                try
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    await _signalWriter.WriteBeginTokenAsync(
                        _options.BeginSignalPath + ".tmp",
                        _options.BeginSignalPath,
                        _options.BeginToken,
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    bool canRetry;
                    lock (_lock)
                    {
                        canRetry = !_completed && !_authorized &&
                            _state == WgcContinuousManagedSessionState.Authorizing;
                        if (canRetry)
                        {
                            _state = WgcContinuousManagedSessionState.WaitingForAuthorization;
                            _authorizationTask = null;
                        }
                    }

                    // Idempotent cleanup: if the final signal was never
                    // published, ensure no tmp file containing the token is
                    // left behind by a writer that observed cancellation.
                    if (!File.Exists(_options.BeginSignalPath))
                        SafeDelete(_options.BeginSignalPath + ".tmp");

                    ownerTcs.TrySetResult(false);
                    return;
                }
                catch
                {
                    _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Failed, "authorize_write_failed");
                    ownerTcs.TrySetResult(false);
                    return;
                }

                bool authorized;
                lock (_lock)
                {
                    if (_completed)
                    {
                        // Finalization cleaned up or is cleaning up; ensure our
                        // just-written file is gone so an end-of-session artifact
                        // cannot be reused.
                        CleanupControlFiles();
                        authorized = false;
                    }
                    else
                    {
                        _authorized = true;
                        _state = WgcContinuousManagedSessionState.Authorized;
                        authorized = true;
                    }
                }

                ownerTcs.TrySetResult(authorized);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeAuthorizationLinkedCtsCount);
        }
    }

    private bool CreateStopSignal()
    {
        try
        {
            var dir = Path.GetDirectoryName(_options.StopSignalPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(_options.StopSignalPath, "");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool CleanupSignalsBeforeStart()
    {
        try
        {
            DeleteIfExists(_options.BeginSignalPath);
            DeleteIfExists(_options.BeginSignalPath + ".tmp");
            DeleteIfExists(_options.StopSignalPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private async Task RunStdoutReader()
    {
        try
        {
            await using var stream = _process.StandardOutputStream;
            var reader = new BoundedStdoutReader(stream, _sessionCts.Token, OnBlockParsed, OnProtocolViolation);
            await reader.ReadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during finalization.
        }
        catch
        {
            // Stdout reader failures must not stop the recording flow.
        }
    }

    private void OnBlockParsed(WgcContinuousEvent evt)
    {
        lock (_lock)
        {
            _events.Add(evt);

            // Any capture event (other than explicit FAIL) before successful
            // authorization is a consent/protocol violation.
            if (!_authorized && evt.Result != ContinuousEventResult.Fail)
            {
                _protocolViolation = "event_before_authorization";
                _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Failed, "event_before_authorization");
                return;
            }
        }

        try { EventReceived?.Invoke(evt); }
        catch { /* observers must not affect flow */ }

        switch (evt.Result)
        {
            case ContinuousEventResult.Started:
                OnStartedEvent(evt);
                break;
            case ContinuousEventResult.Progress:
                OnProgressEvent(evt);
                break;
            case ContinuousEventResult.FirstFrame:
                OnFirstFrameEvent(evt);
                break;
            case ContinuousEventResult.Ok:
            case ContinuousEventResult.Stopped:
            case ContinuousEventResult.Fail:
                OnTerminalEvent(evt);
                break;
        }
    }

    private void OnProtocolViolation(string category)
    {
        lock (_lock)
        {
            _protocolViolation = category;
        }
        _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Failed, category);
    }

    private async Task RunStderrReader()
    {
        try
        {
            await using var stream = _process.StandardErrorStream;
            var reader = new BoundedStderrReader(stream, _sessionCts.Token, _stderrBuffer, MaxStderrChars);
            await reader.ReadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during finalization.
        }
        catch
        {
            // Stderr reader failures must not stop the recording flow.
        }
    }

    private void OnStartedEvent(WgcContinuousEvent evt)
    {
        string? violation = null;
        lock (_lock)
        {
            // STARTED is only valid after successful authorization. The
            // ProcessBlock gate already rejects capture events before
            // authorization, so reaching here in any other state is a protocol
            // violation that we ignore for state-machine purposes.
            if (_state == WgcContinuousManagedSessionState.Authorized)
            {
                string expectedMethod = _options.TargetKind == WgcContinuousTargetKind.Window
                    ? "WGC_D3D11_WINDOW_FRAME_STREAM"
                    : _options.TargetKind == WgcContinuousTargetKind.Region
                        ? "WGC_D3D11_REGION_FRAME_STREAM"
                        : "WGC_D3D11_FRAME_STREAM";
                bool dimensionsMatch = _options.TargetKind != WgcContinuousTargetKind.Region ||
                    (evt.Width.HasValue && evt.Height.HasValue &&
                     evt.Width.Value == _options.RegionWidth &&
                     evt.Height.Value == _options.RegionHeight);
                bool encoderModeValid = evt.EncoderMode is "software" or "hardware";
                bool encoderReasonValid = evt.EncoderSelectionReason is "software_default" or "hardware_selected" or
                    "hardware_unavailable_fallback" or "hardware_init_failed_fallback" or
                    "hardware_unverified_fallback";
                bool encoderSelectionMatch = encoderModeValid && encoderReasonValid &&
                    (evt.EncoderMode == "hardware"
                        ? evt.EncoderSelectionReason == "hardware_selected"
                        : evt.EncoderSelectionReason != "hardware_selected");
                if (!string.Equals(evt.CaptureMethod, expectedMethod, StringComparison.Ordinal))
                    violation = _options.TargetKind == WgcContinuousTargetKind.Region
                        ? "region_started_metadata_mismatch"
                        : "capture_method_mismatch";
                else if (!dimensionsMatch)
                    violation = _options.TargetKind == WgcContinuousTargetKind.Region
                        ? "region_started_metadata_mismatch"
                        : "capture_method_mismatch";
                else if (!encoderSelectionMatch || evt.HasDuplicateFields ||
                         !EncoderSelectionMatchesRequestedMode(evt.EncoderMode, evt.EncoderSelectionReason))
                    violation = "encoder_selection_policy_mismatch";
                else
                    _state = WgcContinuousManagedSessionState.Started;
            }
        }

        if (violation != null)
            OnProtocolViolation(violation);
    }

    private void OnFirstFrameEvent(WgcContinuousEvent evt)
    {
        string? violation = null;
        FirstFrameObservation? observation = null;

        lock (_lock)
        {
            // Trust-boundary validation happens BEFORE the exactly-once gate is
            // consumed and before any observer runs. A malformed, out-of-order,
            // or duplicate FIRST_FRAME is a protocol violation that fails the
            // session closed; it must never become credible recording evidence.
            if (_state != WgcContinuousManagedSessionState.Started)
            {
                // FIRST_FRAME is only valid after the helper declared STARTED.
                // The OnBlockParsed gate already rejects pre-authorization
                // events; reaching here in a terminal/completing state is an
                // ordering violation as well.
                violation = "first_frame_before_started";
            }
            else if (_completed || _seenTerminalEvent)
            {
                violation = "first_frame_after_terminal";
            }
            else if (!IsValidFirstFrameEvent(evt))
            {
                violation = "first_frame_invalid";
            }
            else if (_firstFrameObserved != 0)
            {
                // A second explicit FIRST_FRAME (valid or not) is a protocol
                // anomaly: fail closed rather than silently accepting it.
                violation = "duplicate_first_frame";
            }
            else
            {
                // Exactly-once gate shared with the legacy progress fallback:
                // once explicit evidence is published, a later PROGRESS with
                // FramesCaptured > 0 must not publish a second observation.
                Interlocked.Exchange(ref _firstFrameObserved, 1);
                observation = new FirstFrameObservation
                {
                    EvidenceKind = "wgc_continuous_first_frame",
                    FrameNumber = evt.FrameNumber!.Value,
                    // No encoded bytes exist yet at source-frame time; bytes
                    // evidence stays zero rather than fabricating a value.
                    TotalSizeBytes = 0,
                    OutTimeUs = evt.ElapsedMs!.Value * 1000
                };
                _firstFrameObservation = observation;
            }
        }

        if (violation != null)
        {
            OnProtocolViolation(violation);
            return;
        }

        if (observation != null)
        {
            try { FirstFrameObserved?.Invoke(observation); }
            catch { /* observers must not affect flow */ }
        }
    }

    /// <summary>
    /// Strict live validation of a FIRST_FRAME block at the trust boundary.
    /// Mirrors the parser's documented field rules and the session's strict
    /// CaptureMethod-style policy for the authenticated Stage field: the frame
    /// number must be a parsed positive integer, the elapsed time a parsed
    /// non-negative integer, and Stage must be the documented capturing stage.
    /// No defaults are fabricated for missing or unparsable values.
    /// </summary>
    private static bool IsValidFirstFrameEvent(WgcContinuousEvent evt)
    {
        if (evt.FrameNumberParseFailed || !evt.FrameNumber.HasValue || evt.FrameNumber.Value <= 0)
            return false;
        if (evt.ElapsedMsParseFailed || !evt.ElapsedMs.HasValue || evt.ElapsedMs.Value < 0)
            return false;
        if (!string.Equals(evt.Stage, "Capturing", StringComparison.Ordinal))
            return false;
        return true;
    }

    private void OnProgressEvent(WgcContinuousEvent evt)
    {
        if (evt.FramesCaptured.GetValueOrDefault() <= 0)
            return;

        lock (_lock)
        {
            // First-frame evidence must only be published after the helper has
            // declared STARTED and the session has left the authorization
            // states. A completed/completing session (e.g. failed by a
            // malformed explicit FIRST_FRAME) must never be rescued by a later
            // legacy progress fallback.
            if (_state != WgcContinuousManagedSessionState.Started ||
                _completed ||
                _seenTerminalEvent)
                return;
        }

        if (Interlocked.Exchange(ref _firstFrameObserved, 1) != 0)
            return;

        _firstFrameObservation = new FirstFrameObservation
        {
            EvidenceKind = "wgc_continuous_progress",
            FrameNumber = evt.FramesCaptured!.Value,
            TotalSizeBytes = evt.BytesWritten.GetValueOrDefault(),
            OutTimeUs = evt.ElapsedMs.HasValue ? evt.ElapsedMs.Value * 1000 : null
        };

        try { FirstFrameObserved?.Invoke(_firstFrameObservation); }
        catch { /* observers must not affect flow */ }
    }

    private void OnTerminalEvent(WgcContinuousEvent evt)
    {
        lock (_lock)
        {
            // Any terminal event (OK/STOPPED/FAIL) closes the window in which
            // first-frame evidence may be accepted.
            _seenTerminalEvent = true;
        }

        if (evt.Result == ContinuousEventResult.Fail)
        {
            lock (_lock)
            {
                // Preserve the helper's authenticated terminal category. A
                // later process-exit/timeout race may only add context; it
                // must not replace window lifecycle evidence with a generic
                // session or exit reason.
                _failureReason = !string.IsNullOrEmpty(evt.ErrorCode)
                    ? evt.ErrorCode
                    : !string.IsNullOrEmpty(evt.StopReason)
                        ? evt.StopReason
                        : "helper_reported_failure";
            }
        }

        // Do not signal the watcher here. Terminal events are parsed and stored;
        // finalization is driven by process exit, timeout, or an explicit stop
        // request. Signaling on OK/STOPPED would let a helper that reports
        // success but refuses to exit win as Success before the timeout fires.
    }

    private async Task RunWatcher()
    {
        try
        {
            var timeoutTask = Task.Delay(_options.ProcessTimeoutMs, _sessionCts.Token);
            var exitTask = _process.WaitForExitAsync(_sessionCts.Token);
            var finalizeTask = _finalizeSignal.Task;

            var completed = await Task.WhenAny(timeoutTask, exitTask, finalizeTask).ConfigureAwait(false);

            // Prefer natural process exit and explicit finalize over timeout to
            // avoid races where all three complete around the same moment.
            if (exitTask.IsCompleted)
            {
                await DrainStdoutReaderAsync();
                var suggested = WgcContinuousManagedSessionState.Success;
                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(_failureReason) || !string.IsNullOrEmpty(_protocolViolation))
                        suggested = WgcContinuousManagedSessionState.Failed;
                }
                _ = TriggerCompletionAsync(suggested, null);
            }
            else if (finalizeTask.IsCompleted)
            {
                var suggested = _stopRequested
                    ? WgcContinuousManagedSessionState.Stopping
                    : WgcContinuousManagedSessionState.Success;

                // If a lifecycle/protocol failure was already recorded (e.g.
                // authorize write failed, stop signal creation failed, input
                // bounds exceeded), do not let the natural terminal event win
                // as Success/Stopped.
                lock (_lock)
                {
                    if (!string.IsNullOrEmpty(_failureReason) || !string.IsNullOrEmpty(_protocolViolation))
                        suggested = WgcContinuousManagedSessionState.Failed;
                }

                _ = TriggerCompletionAsync(suggested, null);
            }
            else
            {
                _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Failed, "process_timeout");
            }
        }
        catch (OperationCanceledException)
        {
            _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Cancelled, "cancelled");
        }
        catch
        {
            _ = TriggerCompletionAsync(WgcContinuousManagedSessionState.Failed, "watcher_error");
        }
    }

    private async Task DrainStdoutReaderAsync()
    {
        if (_stdoutReader != null)
        {
            try
            {
                using var drainCts = new CancellationTokenSource(ReaderDrainTimeout);
                await _stdoutReader.WaitAsync(drainCts.Token).ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }
    }

    private void TerminateLateProcess()
    {
        try
        {
            _process.KillEntireTree();
        }
        catch { /* best effort */ }

        try
        {
            using var cts = new CancellationTokenSource(LateProcessKillTimeout);
            _process.WaitForExitAsync(cts.Token).Wait(cts.Token);
        }
        catch { /* best effort */ }

        // Release the process handle/wrapper so a late process does not keep
        // native resources alive. Repeated Dispose calls are safe.
        try
        {
            _process.Dispose();
        }
        catch { /* best effort */ }
    }

    private Task TriggerCompletionAsync(WgcContinuousManagedSessionState suggestedState, string? failureReason)
    {
        if (!TryEnterCompletionWithReason(suggestedState, failureReason))
            return Task.CompletedTask;

        return Task.Run(() => FinalizeAsync(suggestedState, failureReason));
    }

    private bool TryEnterCompletionWithReason(WgcContinuousManagedSessionState suggestedState, string? failureReason)
    {
        lock (_lock)
        {
            if (_completed)
                return false;
            _completed = true;
            if (!string.IsNullOrEmpty(failureReason) && string.IsNullOrEmpty(_failureReason))
                _failureReason = failureReason;
            return true;
        }
    }

    private async Task FinalizeAsync(WgcContinuousManagedSessionState suggestedState, string? failureReason)
    {
        WgcContinuousManagedSessionState finalState;
        WgcContinuousSessionSummary summary;

        try
        {
            // Allow stdout/stderr readers to drain buffered events before
            // cancellation. This is especially important when the process exits
            // quickly and the watcher would otherwise cancel the readers before
            // they parse the terminal event and bounded stderr tail.
            await DrainStdoutReaderAsync();

            if (_stderrReader != null)
            {
                try
                {
                    using var drainCts = new CancellationTokenSource(ReaderDrainTimeout);
                    await _stderrReader.WaitAsync(drainCts.Token).ConfigureAwait(false);
                }
                catch { /* proceed */ }
            }

            _sessionCts.Cancel();

            if (_stopRequested)
                CreateStopSignal();

            // For failure/timeout/cancellation paths, terminate the tree immediately
            // instead of waiting for a graceful exit that may never come.
            bool forceKill = suggestedState is WgcContinuousManagedSessionState.Failed
                or WgcContinuousManagedSessionState.Cancelled;

            if (forceKill && !_process.HasExited)
                _process.KillEntireTree();

            // Give the helper a bounded window to exit naturally (or after kill).
            try
            {
                using var cts = new CancellationTokenSource(ProcessExitAfterKillTimeout);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                if (!forceKill)
                    _process.KillEntireTree();

                try
                {
                    using var cts = new CancellationTokenSource(ProcessExitAfterKillTimeout);
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch { /* best effort */ }
            }

            _exitCode = _process.ExitCode;

            lock (_lock)
            {
                summary = WgcContinuousEventStreamParser.ValidateAndSummarize(_events);
            }

            finalState = DetermineFinalState(suggestedState, summary);
        }
        catch (Exception)
        {
            // The finalizer must never fault: ensure a terminal result is still
            // produced even if parsing, path normalization, or file-system checks
            // throw.
            finalState = WgcContinuousManagedSessionState.Failed;
            lock (_lock)
            {
                _state = finalState;
            }
            summary = new WgcContinuousSessionSummary
            {
                State = ContinuousSessionState.Failed,
                ValidationErrors = { "finalize_exception" }
            };
            if (string.IsNullOrEmpty(_failureReason))
                _failureReason = "finalize_exception";
        }

        try
        {
            CleanupControlFiles();
        }
        catch { /* best effort */ }

        WgcContinuousSessionResult result;
        try
        {
            result = BuildResult(finalState, summary);
        }
        catch
        {
            // Synchronize finalState with the failed result so session.State
            // can never report Success while CompletionTask.Result.State is Failed.
            finalState = WgcContinuousManagedSessionState.Failed;
            result = new WgcContinuousSessionResult
            {
                State = finalState,
                FailurePhase = "lifecycle",
                FailureCategory = "finalize_exception",
                ExitCode = _exitCode,
                OutputPath = _options.OutputPath
            };
        }

        lock (_lock)
        {
            _state = finalState;
        }

        _completionTcs.TrySetResult(result);
    }

    private WgcContinuousManagedSessionState DetermineFinalState(
        WgcContinuousManagedSessionState suggested,
        WgcContinuousSessionSummary summary)
    {
        // Terminal-state priority:
        // 1. Cancelled, once recorded, is irreversible.
        // 2. Any lifecycle/protocol failure that already won the exactly-once
        //    completion gate must stay Failed regardless of what IPC drain later
        //    parsed.
        // 3. Only when no higher-priority failure exists do we trust Success/Stopped
        //    IPC and apply output authenticity checks.
        // A parsed helper FAIL is stronger than a generic cancellation or
        // process-exit race. The summary is available only after stdout drain,
        // so this check must precede the suggested-state shortcut.
        if (summary.State == ContinuousSessionState.Failed &&
            (!string.IsNullOrEmpty(summary.ErrorCode) ||
             !string.IsNullOrEmpty(summary.StopReason) ||
             !string.IsNullOrEmpty(summary.Reason)))
        {
            return WgcContinuousManagedSessionState.Failed;
        }

        if (suggested == WgcContinuousManagedSessionState.Cancelled)
            return WgcContinuousManagedSessionState.Cancelled;

        lock (_lock)
        {
            if (suggested == WgcContinuousManagedSessionState.Failed ||
                !string.IsNullOrEmpty(_failureReason) ||
                !string.IsNullOrEmpty(_protocolViolation))
            {
                return WgcContinuousManagedSessionState.Failed;
            }
        }

        if (suggested == WgcContinuousManagedSessionState.Success ||
            suggested == WgcContinuousManagedSessionState.Stopping)
        {
            // The helper must have produced a valid success/stopped terminal
            // event before we apply file-system authenticity checks.
            if (summary.State != ContinuousSessionState.Success &&
                summary.State != ContinuousSessionState.Stopped)
            {
                return WgcContinuousManagedSessionState.Failed;
            }

            var (ok, failure) = ValidateSuccessAuthenticity(suggested, summary);
            if (!ok)
            {
                _failureReason = failure;
                return WgcContinuousManagedSessionState.Failed;
            }
        }

        return summary.State switch
        {
            ContinuousSessionState.Success => WgcContinuousManagedSessionState.Success,
            ContinuousSessionState.Stopped => WgcContinuousManagedSessionState.Stopped,
            ContinuousSessionState.Failed => WgcContinuousManagedSessionState.Failed,
            ContinuousSessionState.MalformedSequence => WgcContinuousManagedSessionState.Failed,
            _ => WgcContinuousManagedSessionState.Failed
        };
    }

    private (bool ok, string? failure) ValidateSuccessAuthenticity(
        WgcContinuousManagedSessionState suggested,
        WgcContinuousSessionSummary summary)
    {
        // Stopping that does not produce STOPPED/OK will be handled by the
        // summary parser below; we only validate when the helper claims success.
        if (_exitCode != 0)
            return (false, "non_zero_exit_code");

        string requestedPath;
        try
        {
            requestedPath = Path.GetFullPath(_options.OutputPath);
        }
        catch
        {
            return (false, "invalid_output_path");
        }

        string summaryPath;
        try
        {
            summaryPath = string.IsNullOrEmpty(summary.OutputPath)
                ? string.Empty
                : Path.GetFullPath(summary.OutputPath);
        }
        catch
        {
            return (false, "invalid_output_path");
        }

        if (!string.Equals(requestedPath, summaryPath, StringComparison.OrdinalIgnoreCase))
            return (false, "output_path_mismatch");

        long actualSize;
        try
        {
            var fi = new FileInfo(requestedPath);
            if (!fi.Exists)
                return (false, "missing_output_file");
            if (fi.Length == 0)
                return (false, "empty_output_file");
            actualSize = fi.Length;
        }
        catch
        {
            return (false, "missing_output_file");
        }

        if (summary.HasFileSize && summary.FileSize.HasValue && summary.FileSize.Value != actualSize)
            return (false, "file_size_mismatch");
        if (summary.HasBytesWritten && summary.BytesWritten.HasValue && summary.BytesWritten.Value != actualSize)
            return (false, "bytes_written_mismatch");

        if (_options.TargetKind == WgcContinuousTargetKind.Region &&
            (summary.Width != _options.RegionWidth || summary.Height != _options.RegionHeight ||
             !summary.TerminalDimensionsPresent ||
             !string.Equals(summary.CaptureMethod, "WGC_D3D11_REGION_FRAME_STREAM", StringComparison.Ordinal)))
            return (false, "region_terminal_metadata_mismatch");

        if (!IsValidEncoderSelection(summary.EncoderMode, summary.EncoderSelectionReason) ||
            !EncoderSelectionMatchesRequestedMode(summary.EncoderMode, summary.EncoderSelectionReason))
            return (false, "encoder_selection_policy_mismatch");

        return (true, null);
    }

    private static bool IsValidEncoderSelection(string? mode, string? reason) =>
        mode is "software" or "hardware" &&
        reason is "software_default" or "hardware_selected" or
            "hardware_unavailable_fallback" or "hardware_init_failed_fallback" or
            "hardware_unverified_fallback" &&
        (mode == "hardware" ? reason == "hardware_selected" : reason != "hardware_selected");

    private bool EncoderSelectionMatchesRequestedMode(string? mode, string? reason)
    {
        if (!IsValidEncoderSelection(mode, reason))
            return false;
        if (_options.EncoderMode == WgcEncoderMode.Software)
            return mode == "software" && reason == "software_default";
        return mode == "hardware"
            ? reason == "hardware_selected"
            : reason is "hardware_unavailable_fallback" or
                "hardware_init_failed_fallback" or
                "hardware_unverified_fallback";
    }

    private void CleanupControlFiles()
    {
        SafeDelete(_options.BeginSignalPath);
        SafeDelete(_options.BeginSignalPath + ".tmp");
        SafeDelete(_options.StopSignalPath);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best effort */ }
    }

    private WgcContinuousSessionResult BuildResult(
        WgcContinuousManagedSessionState finalState,
        WgcContinuousSessionSummary summary)
    {
        if (ThrowFromBuildResultForTests)
            throw new InvalidOperationException("Test-induced BuildResult failure.");

        var outputPath = summary.OutputPath ?? _options.OutputPath;
        long fileSize = 0;
        bool exists = false;
        try
        {
            var fi = new FileInfo(outputPath);
            exists = fi.Exists;
            fileSize = fi.Length;
        }
        catch { /* best effort */ }

        var result = new WgcContinuousSessionResult
        {
            State = finalState,
            ExitCode = _exitCode,
            Summary = summary,
            StopRequestedByCaller = _stopRequested,
            StderrTail = _stderrBuffer.ToString(),
            OutputPath = outputPath,
            OutputFileExists = exists,
            OutputFileSizeBytes = fileSize,
            FirstFrameObserved = _firstFrameObservation != null,
            FirstFrameNumber = _firstFrameObservation?.FrameNumber,
            FirstFrameElapsedMs = _firstFrameObservation?.OutTimeUs / 1000
        };

        if (finalState == WgcContinuousManagedSessionState.Failed ||
            finalState == WgcContinuousManagedSessionState.Cancelled)
        {
            var (phase, category) = ClassifyFailure(summary);
            result.FailurePhase = phase;
            result.FailureCategory = category;
        }

        return result;
    }

    private (string phase, string category) ClassifyFailure(WgcContinuousSessionSummary summary)
    {
        if (!string.IsNullOrEmpty(_protocolViolation))
            return ("protocol", _protocolViolation);

        // Prefer a specific helper FAIL category over a timeout, non-zero
        // exit, or other generic reason captured by the watcher.
        if (summary.State == ContinuousSessionState.Failed)
        {
            string helperCategory = summary.GetStopReasonForEvidence();
            if (!string.Equals(helperCategory, "error", StringComparison.Ordinal))
                return ("helper_reported_failure", helperCategory);
        }

        var lifecycleCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "process_start_failed",
            "process_timeout",
            "stop_wait_timeout",
            "stop_signal_create_failed",
            "authorize_write_failed",
            "pre_start_cleanup_failed",
            "cancelled",
            "disposed",
            "caller_cancelled",
            "watcher_error",
            "finalize_exception"
        };

        var authenticityCategories = new HashSet<string>(StringComparer.Ordinal)
        {
            "non_zero_exit_code",
            "output_path_mismatch",
            "missing_output_file",
            "empty_output_file",
            "file_size_mismatch",
            "bytes_written_mismatch",
            "invalid_output_path"
        };

        if (!string.IsNullOrEmpty(_failureReason))
        {
            if (lifecycleCategories.Contains(_failureReason))
                return ("lifecycle", _failureReason);
            if (authenticityCategories.Contains(_failureReason))
                return ("output_authenticity", _failureReason);
        }

        if (summary.State == ContinuousSessionState.MalformedSequence)
            return ("ipc_parser", "missing_or_malformed_terminal_event");

        return ("helper_reported_failure", summary.GetStopReasonForEvidence());
    }

    /// <summary>
    /// Tail-bounded character buffer for stderr. Keeps the most recent
    /// characters up to <paramref name="maxLength"/> using a fixed-size ring
    /// buffer so oversized input never allocates a matching-size string.
    /// </summary>
    private sealed class BoundedStringBuilder
    {
        private readonly int _maxLength;
        private readonly char[] _buffer;
        private readonly object _lock = new();
        private int _count;
        private int _head;

        public BoundedStringBuilder(int maxLength)
        {
            _maxLength = Math.Max(0, maxLength);
            _buffer = new char[_maxLength];
        }

        public int Length
        {
            get { lock (_lock) return _count; }
        }

        public void Append(char value)
        {
            lock (_lock)
            {
                if (_maxLength == 0)
                    return;

                if (_count < _maxLength)
                {
                    _count++;
                }
                else
                {
                    _head = (_head + 1) % _maxLength;
                }

                int tail = (_head + _count - 1) % _maxLength;
                _buffer[tail] = value;
            }
        }

        public void Append(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty)
                return;

            lock (_lock)
            {
                foreach (var c in value)
                    AppendCore(c);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _count = 0;
                _head = 0;
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                if (_count == 0)
                    return string.Empty;

                var result = new char[_count];
                for (int i = 0; i < _count; i++)
                {
                    result[i] = _buffer[(_head + i) % _maxLength];
                }
                return new string(result);
            }
        }

        private void AppendCore(char value)
        {
            if (_maxLength == 0)
                return;

            if (_count < _maxLength)
            {
                _count++;
            }
            else
            {
                _head = (_head + 1) % _maxLength;
            }

            int tail = (_head + _count - 1) % _maxLength;
            _buffer[tail] = value;
        }
    }

    /// <summary>
    /// Reads stdout in fixed-size UTF-8 chunks, enforcing memory bounds before
    /// a full oversized line or event block is materialized.
    /// </summary>
    private sealed class BoundedStdoutReader
    {
        private readonly Stream _stream;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<WgcContinuousEvent> _onEvent;
        private readonly Action<string> _onViolation;
        private readonly Decoder _decoder;
        private readonly List<string> _block = new();
        private readonly StringBuilder _line = new();
        private readonly byte[] _byteBuffer;
        private readonly char[] _charBuffer;
        private int _pendingByteCount;
        private bool _completed;
        private bool _violationReported;
        private int _eventCount;
        private bool _pendingCr;

        public BoundedStdoutReader(
            Stream stream,
            CancellationToken cancellationToken,
            Action<WgcContinuousEvent> onEvent,
            Action<string> onViolation)
        {
            _stream = stream;
            _cancellationToken = cancellationToken;
            _onEvent = onEvent;
            _onViolation = onViolation;
            _decoder = Encoding.UTF8.GetDecoder();
            _byteBuffer = new byte[StdoutChunkSize];
            _charBuffer = new char[Encoding.UTF8.GetMaxCharCount(StdoutChunkSize)];
        }

        public async Task ReadAsync()
        {
            while (!_completed)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                int read = await _stream.ReadAsync(_byteBuffer.AsMemory(_pendingByteCount), _cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    _completed = true;
                    // Flush any remaining bytes (handles truncated UTF-8 at EOF).
                    Decode(0, true);
                    FlushLine();
                    FlushBlock();
                    return;
                }

                _pendingByteCount += read;
                int bytesConsumed = Decode(_pendingByteCount, false);

                // Move any leftover bytes to the front of the buffer.
                if (bytesConsumed < _pendingByteCount)
                {
                    _byteBuffer.AsSpan(bytesConsumed, _pendingByteCount - bytesConsumed)
                        .CopyTo(_byteBuffer.AsSpan());
                }
                _pendingByteCount -= bytesConsumed;
            }
        }

        private int Decode(int byteCount, bool flush)
        {
            _decoder.Convert(
                _byteBuffer.AsSpan(0, byteCount),
                _charBuffer.AsSpan(),
                flush,
                out int bytesUsed,
                out int charsUsed,
                out bool _);

            ProcessChars(_charBuffer.AsSpan(0, charsUsed));
            return bytesUsed;
        }

        private void ProcessChars(ReadOnlySpan<char> chars)
        {
            int i = 0;

            // A \r that ended the previous chunk must be resolved against the
            // first character of this chunk before scanning the remainder.
            if (_pendingCr)
            {
                _pendingCr = false;
                if (chars.Length > 0 && chars[0] == '\n')
                {
                    EndLine();
                    i = 1;
                }
                else
                {
                    EndLine();
                }
            }

            for (; i < chars.Length; i++)
            {
                char c = chars[i];

                if (c == '\r')
                {
                    if (i + 1 < chars.Length && chars[i + 1] == '\n')
                    {
                        EndLine();
                        i++; // skip LF
                        continue;
                    }

                    if (i + 1 == chars.Length)
                    {
                        // CR is the last character in this chunk; defer the
                        // line termination until the next chunk resolves it.
                        _pendingCr = true;
                        continue;
                    }

                    EndLine();
                    continue;
                }

                if (c == '\n')
                {
                    EndLine();
                    continue;
                }

                if (_line.Length >= MaxSingleLineLength)
                {
                    ReportViolation("max_stdout_line_length_exceeded");
                    return;
                }

                _line.Append(c);
            }
        }

        private void EndLine()
        {
            _block.Add(_line.ToString());
            _line.Clear();

            if (_block.Count > MaxLinesPerEventBlock)
            {
                ReportViolation("max_lines_per_event_block_exceeded");
                return;
            }

            // A blank line (after trimming) ends the current event block.
            if (_block.Count > 0 && string.IsNullOrWhiteSpace(_block[^1]))
            {
                // Remove the blank separator line before parsing.
                _block.RemoveAt(_block.Count - 1);
                FlushBlock();
            }
        }

        private void FlushLine()
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                EndLine();
            }

            if (_line.Length > 0)
            {
                if (_line.Length > MaxSingleLineLength)
                {
                    ReportViolation("max_stdout_line_length_exceeded");
                    return;
                }

                _block.Add(_line.ToString());
                _line.Clear();

                if (_block.Count > MaxLinesPerEventBlock)
                {
                    ReportViolation("max_lines_per_event_block_exceeded");
                    return;
                }
            }
        }

        private void FlushBlock()
        {
            if (_block.Count == 0)
                return;

            var evt = WgcContinuousEventStreamParser.ParseEventBlock(_block);
            _block.Clear();

            if (evt == null)
                return;

            _eventCount++;
            if (_eventCount > MaxStdoutEvents)
            {
                ReportViolation("max_stdout_events_exceeded");
                return;
            }

            _onEvent(evt);
        }

        private void ReportViolation(string category)
        {
            if (_violationReported)
                return;
            _violationReported = true;
            _completed = true;
            _onViolation(category);
        }
    }

    /// <summary>
    /// Reads stderr in fixed-size UTF-8 chunks, keeping only the most recent
    /// characters up to the configured maximum. Handles incomplete UTF-8
    /// sequences across chunk boundaries without materializing a full huge line.
    /// </summary>
    private sealed class BoundedStderrReader
    {
        private readonly Stream _stream;
        private readonly CancellationToken _cancellationToken;
        private readonly BoundedStringBuilder _buffer;
        private readonly int _maxChars;
        private readonly Decoder _decoder;
        private readonly byte[] _byteBuffer;
        private readonly char[] _charBuffer;
        private int _pendingByteCount;

        public BoundedStderrReader(
            Stream stream,
            CancellationToken cancellationToken,
            BoundedStringBuilder buffer,
            int maxChars)
        {
            _stream = stream;
            _cancellationToken = cancellationToken;
            _buffer = buffer;
            _maxChars = maxChars;
            _decoder = Encoding.UTF8.GetDecoder();
            _byteBuffer = new byte[StdoutChunkSize];
            _charBuffer = new char[Encoding.UTF8.GetMaxCharCount(StdoutChunkSize)];
        }

        public async Task ReadAsync()
        {
            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                int read = await _stream.ReadAsync(_byteBuffer.AsMemory(_pendingByteCount), _cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    Decode(0, true);
                    return;
                }

                _pendingByteCount += read;
                int bytesConsumed = Decode(_pendingByteCount, false);

                if (bytesConsumed < _pendingByteCount)
                {
                    _byteBuffer.AsSpan(bytesConsumed, _pendingByteCount - bytesConsumed)
                        .CopyTo(_byteBuffer.AsSpan());
                }
                _pendingByteCount -= bytesConsumed;
            }
        }

        private int Decode(int byteCount, bool flush)
        {
            _decoder.Convert(
                _byteBuffer.AsSpan(0, byteCount),
                _charBuffer.AsSpan(),
                flush,
                out int bytesUsed,
                out int charsUsed,
                out bool _);

            AppendChars(_charBuffer.AsSpan(0, charsUsed));
            return bytesUsed;
        }

        private void AppendChars(ReadOnlySpan<char> chars)
        {
            // BoundedStringBuilder maintains the tail bound in O(1) per char.
            _buffer.Append(chars);
        }
    }
}

/// <summary>
/// Test seam for the begin-token file write so that authorization I/O can be
/// blocked, observed, and cancelled deterministically without holding the
/// session lock.
/// </summary>
internal interface IAuthorizationSignalWriter
{
    Task WriteBeginTokenAsync(
        string tmpPath,
        string finalPath,
        string token,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production implementation: atomic tmp-file write followed by a move.
/// </summary>
internal sealed class FileAuthorizationSignalWriter : IAuthorizationSignalWriter
{
    public Task WriteBeginTokenAsync(
        string tmpPath,
        string finalPath,
        string token,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            bool committed = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var dir = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // .NET 8 on Windows treats File.Move(source, existingDirectory, overwrite: true)
                // as a rename/replace of the directory with the file, which silently corrupts
                // the signal path. Explicitly reject a directory target so authorization fails
                // deterministically and tests that block writes by creating a directory work.
                if (Directory.Exists(finalPath))
                    throw new IOException($"Begin signal path '{finalPath}' is a directory.");

                await File.WriteAllTextAsync(tmpPath, token, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tmpPath, finalPath, overwrite: true);
                committed = true;
            }
            finally
            {
                // Cleanup is based on whether *this* call committed the move,
                // not on whether finalPath happens to exist for any other reason.
                if (!committed)
                    SafeDelete(tmpPath);
            }
        }, cancellationToken);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best effort */ }
    }
}
