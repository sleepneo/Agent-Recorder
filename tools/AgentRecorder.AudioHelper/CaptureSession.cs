using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Abstraction over a Windows audio capture device. Production implementation
/// uses NAudio WasapiCapture; fake implementations are used for deterministic
/// unit tests.
/// </summary>
internal interface IAudioInput : IDisposable
{
    WaveFormat? Format { get; }
    event EventHandler<WaveInEventArgs>? DataAvailable;
    event EventHandler<StoppedEventArgs>? RecordingStopped;
    void StartRecording();
    void StopRecording();
}

/// <summary>
/// NAudio-backed WASAPI capture input. Keeps all NAudio types inside the
/// helper so the parent process never references them.
/// </summary>
internal sealed class WasapiAudioInput : IAudioInput
{
    private readonly WasapiCapture _capture;

    public WaveFormat? Format => _capture.WaveFormat;

    public event EventHandler<WaveInEventArgs>? DataAvailable
    {
        add => _capture.DataAvailable += value;
        remove => _capture.DataAvailable -= value;
    }

    public event EventHandler<StoppedEventArgs>? RecordingStopped
    {
        add => _capture.RecordingStopped += value;
        remove => _capture.RecordingStopped -= value;
    }

    private WasapiAudioInput(WasapiCapture capture)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    public static (IAudioInput? Input, string? ErrorCode, string? Reason) Open(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
            return (null, "audio_endpoint_not_found", "Endpoint id is empty");

        MMDevice? device = null;
        MMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(endpointId);
            if (device == null)
                return (null, "audio_endpoint_not_found", "Endpoint not found");

            return device.State switch
            {
                DeviceState.NotPresent => (null, "audio_endpoint_not_found", "Endpoint not present"),
                DeviceState.Unplugged => (null, "audio_endpoint_inactive", "Endpoint unplugged"),
                DeviceState.Disabled => (null, "audio_endpoint_inactive", "Endpoint disabled"),
                DeviceState.Active => TryCreateCapture(device),
                _ => (null, "audio_endpoint_inactive", $"Endpoint state is {device.State}")
            };
        }
        catch (Exception ex)
        {
            return (null, "audio_endpoint_not_found", ex.Message);
        }
        finally
        {
            // The device COM object is owned by the WasapiCapture when active.
            // Dispose the enumerator only; disposing the device here would
            // invalidate the capture stream.
            enumerator?.Dispose();
        }
    }

    private static (IAudioInput? Input, string? ErrorCode, string? Reason) TryCreateCapture(MMDevice device)
    {
        try
        {
            var capture = new WasapiCapture(device);
            return (new WasapiAudioInput(capture), null, null);
        }
        catch (Exception ex)
        {
            return (null, "audio_format_unsupported", ex.Message);
        }
    }

    public void StartRecording() => _capture.StartRecording();

    public void StopRecording()
    {
        try { _capture.StopRecording(); }
        catch { /* StopRecording must be safe to call repeatedly */ }
    }

    public void Dispose()
    {
        try { _capture.Dispose(); }
        catch { /* best effort */ }
    }
}

/// <summary>
/// Owns the WASAPI capture session: wires up the audio input, writes to the
/// temporary WAV file, emits the IPC event stream, and converges to exactly
/// one terminal event.
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(8);

    private readonly AudioHelperOptions _options;
    private readonly PathCheckResult _paths;
    private readonly EventWriter _events;
    private readonly StopWatcher _watcher;
    private readonly CancellationTokenSource _cts;
    private readonly ManualResetEventSlim _completed = new(false);
    private readonly Func<(IAudioInput? Input, string? ErrorCode, string? Reason)>? _inputFactory;

    private readonly object _stateLock = new();
    private readonly object _writerLock = new();

    private IAudioInput? _input;
    private WaveFileWriter? _writer;
    private WaveFormat? _waveFormat;

    private long _bytesWritten;
    private long _firstCallbackTimestamp;
    private long _lastCallbackTimestamp;
    private long _lastProgressTimestamp;
    private long _firstSampleAnchorTicks;
    private long _stopTimestamp;

    private long _lastProgressBytes;
    private long _lastProgressElapsedMs;
    private long _lastProgressWallElapsedMs;
    private long _lastProgressGapMs;

    private int _startedEventRaised;
    private int _terminalEventRaised;
    private int _userStopRequested;
    private long _exitCode = 1;

    private string? _pendingErrorCode;
    private string _pendingReason = "";
    private string _pendingPartialPath = "";

    public CaptureSession(AudioHelperOptions options, PathCheckResult paths, EventWriter events, StopWatcher watcher, CancellationTokenSource cts)
        : this(options, paths, events, watcher, cts, null) { }

    internal CaptureSession(AudioHelperOptions options, PathCheckResult paths, EventWriter events, StopWatcher watcher, CancellationTokenSource cts, Func<(IAudioInput? Input, string? ErrorCode, string? Reason)>? inputFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _cts = cts ?? throw new ArgumentNullException(nameof(cts));
        _inputFactory = inputFactory;
    }

    public int Run()
    {
        try
        {
            RunCore();
            _completed.Wait(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal stop path; ensure we still emit a terminal event.
            ConvergeTerminal(userRequested: true);
            _completed.Wait(StopWaitTimeout);
        }
        catch (Exception ex)
        {
            ConvergeTerminal(userRequested: false, "audio_helper_runtime_failure", ex.Message, "");
            _completed.Wait(StopWaitTimeout);
        }

        return (int)Interlocked.Read(ref _exitCode);
    }

    private void RunCore()
    {
        var (input, errorCode, reason) = _inputFactory != null
            ? _inputFactory()
            : WasapiAudioInput.Open(_options.EndpointId);
        if (input == null)
        {
            ConvergeTerminal(userRequested: false, errorCode ?? "audio_endpoint_not_found", reason ?? "unknown", "");
            return;
        }

        _input = input;
        var format = input.Format ?? throw new InvalidOperationException("Audio input has no wave format");
        _waveFormat = format;

        Stream partialStream;
        try
        {
            partialStream = _paths.OpenPartialStream?.Invoke()
                ?? throw new InvalidOperationException("Partial output stream is not configured");
        }
        catch (Exception ex)
        {
            ConvergeTerminal(userRequested: false, "audio_output_conflict", "Failed to reserve partial output file: " + ex.Message, _paths.PartialPath);
            return;
        }

        try
        {
            _writer = new WaveFileWriter(partialStream, format);
        }
        catch (Exception ex)
        {
            try { partialStream.Dispose(); } catch { }
            ConvergeTerminal(userRequested: false, "audio_writer_finalize_failed", "Failed to initialize WAV writer: " + ex.Message, _paths.PartialPath);
            return;
        }

        input.DataAvailable += OnDataAvailable;
        input.RecordingStopped += OnRecordingStopped;
        input.StartRecording();
        _watcher.Start();

        // If the caller cancels, request a graceful stop.
        _cts.Token.Register(() => RequestStop());
    }

    public void RequestStop()
    {
        if (Interlocked.Exchange(ref _userStopRequested, 1) != 0)
            return;
        _input?.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        if (Interlocked.CompareExchange(ref _terminalEventRaised, 0, 0) != 0)
            return;

        long now = Stopwatch.GetTimestamp();
        WaveFormat? format;
        long bytesWrittenAfter;

        lock (_writerLock)
        {
            var writer = _writer;
            if (writer == null)
                return;

            format = writer.WaveFormat;

            try
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
            }
            catch (Exception ex)
            {
                SetPendingError("audio_write_failure", "Failed to write audio sample: " + ex.Message, _paths.PartialPath);
                _input?.StopRecording();
                return;
            }

            bytesWrittenAfter = Interlocked.Add(ref _bytesWritten, e.BytesRecorded);

            if (Interlocked.CompareExchange(ref _firstCallbackTimestamp, now, 0) == 0)
            {
                _lastCallbackTimestamp = now;
                _lastProgressTimestamp = now;
                double packetSeconds = e.BytesRecorded / (double)format.AverageBytesPerSecond;
                long packetTicks = (long)(packetSeconds * Stopwatch.Frequency);
                _firstSampleAnchorTicks = now - packetTicks;
            }
            else
            {
                _lastCallbackTimestamp = now;
            }
        }

        if (_firstCallbackTimestamp == now)
        {
            EmitStarted(format, _firstSampleAnchorTicks, bytesWrittenAfter);
        }

        long last = _lastProgressTimestamp;
        var elapsedSinceLast = Stopwatch.GetElapsedTime(last, now);
        if (elapsedSinceLast > ProgressInterval)
        {
            if (Interlocked.CompareExchange(ref _lastProgressTimestamp, now, last) == last)
            {
                TryEmitProgress(bytesWrittenAfter, _firstCallbackTimestamp);
            }
        }

        if (_userStopRequested != 0)
        {
            _input?.StopRecording();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            SetPendingError("audio_capture_error", e.Exception.Message, _paths.PartialPath);
        }

        ConvergeTerminal(userRequested: _userStopRequested != 0);
    }

    private void ConvergeTerminal(bool userRequested, string? initialErrorCode = null, string initialReason = "", string? initialPartialPath = null)
    {
        if (initialErrorCode != null)
            SetPendingError(initialErrorCode, initialReason, initialPartialPath ?? "");

        if (Interlocked.Exchange(ref _terminalEventRaised, 1) != 0)
            return;

        // Owner-only path: stop input, capture the monotonic stop instant, finalize
        // the writer, and emit exactly one terminal event.
        try { _input?.StopRecording(); } catch { }

        long stopTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _stopTimestamp, stopTimestamp);

        WaveFileWriter? writer;
        lock (_writerLock)
        {
            writer = _writer;
            _writer = null;
        }

        long bytesWritten = Interlocked.Read(ref _bytesWritten);

        if (writer != null)
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception ex)
            {
                SetPendingError("audio_writer_finalize_failed", "Failed to finalize WAV writer: " + ex.Message, _paths.PartialPath);
            }
        }

        string? errorCode;
        string reason;
        string partialPath;
        lock (_stateLock)
        {
            errorCode = _pendingErrorCode;
            reason = _pendingReason;
            partialPath = _pendingPartialPath;
        }

        if (errorCode == null && bytesWritten == 0)
        {
            errorCode = "audio_no_packets_captured";
            reason = "No audio packets were captured";
            partialPath = _paths.PartialPath;
        }

        if (errorCode != null)
        {
            EmitFailEvent(errorCode, reason, partialPath);
            CleanupPartial();
            _completed.Set();
            return;
        }

        try
        {
            if (File.Exists(_paths.CanonicalPath))
            {
                EmitFailEvent("audio_output_conflict", "Output file appeared after capture", _paths.PartialPath);
                CleanupPartial();
                _completed.Set();
                return;
            }

            File.Move(_paths.PartialPath, _paths.CanonicalPath);
            Interlocked.Exchange(ref _exitCode, 0);

            var info = BuildTerminalEventInfo(bytesWritten, stopTimestamp);
            if (userRequested)
                _events.Stopped(info);
            else
                _events.Ok(info);
        }
        catch (Exception ex)
        {
            EmitFailEvent("audio_publish_failed", "Failed to publish output file: " + ex.Message, _paths.PartialPath);
        }
        finally
        {
            _completed.Set();
        }
    }

    private void SetPendingError(string errorCode, string reason, string partialPath)
    {
        lock (_stateLock)
        {
            if (_pendingErrorCode == null)
            {
                _pendingErrorCode = errorCode;
                _pendingReason = reason;
                _pendingPartialPath = partialPath;
            }
        }
    }

    private void EmitStarted(WaveFormat format, long anchorTicks, long bytesWritten)
    {
        if (Interlocked.Exchange(ref _startedEventRaised, 1) != 0)
            return;

        _events.Started(new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = format.BitsPerSample,
            FirstSampleAnchorTicks = anchorTicks,
            TimestampFrequency = Stopwatch.Frequency,
            BytesWritten = bytesWritten,
            CaptureMethod = "WASAPI_SHARED_CAPTURE"
        });
    }

    private void TryEmitProgress(long bytesWritten, long firstCallbackTimestamp)
    {
        var info = BuildEventInfo(bytesWritten, firstCallbackTimestamp, Stopwatch.GetTimestamp());

        if (info.BytesWritten < _lastProgressBytes ||
            info.ElapsedMs < _lastProgressElapsedMs ||
            info.WallElapsedMs < _lastProgressWallElapsedMs ||
            info.EstimatedGapMs < _lastProgressGapMs)
        {
            // Regression values would be a protocol error; ignore the progress tick.
            return;
        }

        _lastProgressBytes = info.BytesWritten;
        _lastProgressElapsedMs = info.ElapsedMs;
        _lastProgressWallElapsedMs = info.WallElapsedMs;
        _lastProgressGapMs = info.EstimatedGapMs;

        _events.Progress(info);
    }

    private AudioHelperEventInfo BuildTerminalEventInfo(long bytesWritten, long stopTimestamp)
    {
        var info = BuildEventInfo(bytesWritten, _firstCallbackTimestamp, stopTimestamp);
        info.DurationMs = info.ElapsedMs;
        return info;
    }

    private AudioHelperEventInfo BuildEventInfo(long bytesWritten, long firstCallbackTimestamp, long stopTimestamp)
    {
        var format = _waveFormat;
        long elapsedMs = 0;
        long wallElapsedMs = 0;
        long estimatedGapMs = 0;

        if (format != null && format.AverageBytesPerSecond > 0)
        {
            elapsedMs = (long)(bytesWritten / (double)format.AverageBytesPerSecond * 1000.0);
        }

        if (firstCallbackTimestamp > 0)
        {
            wallElapsedMs = (long)Stopwatch.GetElapsedTime(firstCallbackTimestamp, stopTimestamp).TotalMilliseconds;
            estimatedGapMs = Math.Max(0, wallElapsedMs - elapsedMs);
        }

        return new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            SampleRate = format?.SampleRate ?? 0,
            Channels = format?.Channels ?? 0,
            BitsPerSample = format?.BitsPerSample ?? 0,
            BytesWritten = bytesWritten,
            ElapsedMs = elapsedMs,
            WallElapsedMs = wallElapsedMs,
            EstimatedGapMs = estimatedGapMs,
            DurationMs = elapsedMs,
            FirstSampleAnchorTicks = _firstSampleAnchorTicks,
            TimestampFrequency = Stopwatch.Frequency,
            PartialOutputPath = _paths.PartialPath
        };
    }

    private void EmitFailEvent(string errorCode, string reason, string partialPath)
    {
        Interlocked.Exchange(ref _exitCode, 1);

        _events.Fail(new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            ErrorCode = errorCode,
            Reason = reason,
            PartialOutputPath = partialPath,
            BytesWritten = Interlocked.Read(ref _bytesWritten),
            FirstSampleAnchorTicks = _firstSampleAnchorTicks,
            TimestampFrequency = Stopwatch.Frequency
        });
    }

    private void CleanupPartial()
    {
        try { if (File.Exists(_paths.PartialPath)) File.Delete(_paths.PartialPath); }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        RequestStop();

        try
        {
            // Fast path: if neither input nor writer was ever initialized,
            // the session never ran; no need to wait for terminal convergence.
            if (_input == null && _writer == null)
            {
                _completed.Set();
            }
            else if (!_completed.Wait(StopWaitTimeout))
            {
                _completed.Set();
            }
        }
        catch { /* best effort */ }

        WaveFileWriter? writer;
        lock (_writerLock)
        {
            writer = _writer;
            _writer = null;
        }

        try { writer?.Dispose(); } catch { }
        try { _input?.Dispose(); } catch { }
        _completed.Dispose();
    }
}
