using System.Diagnostics;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

internal delegate INativeAudioRecorder NativeAudioRecorderFactory();

internal interface INativeAudioRecorder : IDisposable
{
    Task InitializeAsync(NativeAudioRecorderRequest request, CancellationToken cancellationToken);
    Task<NativeAudioRecorderFormat> StartAsync(string partialPath, CancellationToken cancellationToken);
    Task WaitForRecordingFailureAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<NativeAudioRecorderFinalized> FinalizeAsync(string partialPath, CancellationToken cancellationToken);
}

internal sealed record NativeAudioRecorderRequest(
    string EndpointId,
    string RecordingId,
    string PartialPath);

internal sealed record NativeAudioRecorderFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample);

internal sealed record NativeAudioRecorderFinalized(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long BytesWritten,
    long DurationMs);

internal sealed class NativeAudioRecorderException : Exception
{
    public NativeAudioRecorderException(
        string errorCode,
        string message,
        int? hresult = null,
        Exception? innerException = null,
        string? sourceEvent = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        HResultValue = hresult;
        SourceEvent = sourceEvent;
    }

    public string ErrorCode { get; }
    public int? HResultValue { get; }
    public string? SourceEvent { get; }
}

internal sealed class NativeMediaCaptureSession : IDisposable
{
    private const int ExitOk = 0;
    private const int ExitRuntimeFailure = 6;
    private const int NativeSampleRate = 16000;
    private const int NativeChannels = 1;
    private const int NativeBitsPerSample = 16;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(8);

    private readonly AudioHelperOptions _options;
    private readonly PathCheckResult _outputCheck;
    private readonly EventWriter _events;
    private readonly StopWatcher _watcher;
    private readonly CancellationTokenSource _cts;
    private readonly NativeAudioRecorderFactory _recorderFactory;
    private readonly TimeSpan _cleanupTimeout;
    private readonly object _lock = new();

    private INativeAudioRecorder? _recorder;
    private Task? _runTask;
    private string _stage = "not_started";
    private int _disposed;
    private int _terminalRaised;
    private int _startedRaised;
    private long _startTimestamp;

    public NativeMediaCaptureSession(
        AudioHelperOptions options,
        PathCheckResult outputCheck,
        EventWriter events,
        StopWatcher watcher,
        CancellationTokenSource cancellationTokenSource,
        NativeAudioRecorderFactory? recorderFactory = null,
        TimeSpan? cleanupTimeout = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _outputCheck = outputCheck ?? throw new ArgumentNullException(nameof(outputCheck));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _cts = cancellationTokenSource ?? throw new ArgumentNullException(nameof(cancellationTokenSource));
        _recorderFactory = recorderFactory ?? (() => new MediaCaptureNativeAudioRecorder());
        _cleanupTimeout = cleanupTimeout ?? DefaultCleanupTimeout;
    }

    public async Task<int> RunAsync()
    {
        if (_runTask != null)
            throw new InvalidOperationException("NativeMediaCaptureSession can only be run once.");

        _runTask = RunCoreAsync();
        return await ((Task<int>)_runTask).ConfigureAwait(false);
    }

    private async Task<int> RunCoreAsync()
    {
        Task? progressTask = null;
        INativeAudioRecorder? recorder = null;

        try
        {
            _stage = "reserve_output";
            using (_outputCheck.OpenPartialStream?.Invoke() ?? throw new IOException("Partial output stream is unavailable"))
            {
            }

            _stage = "initialize";
            recorder = _recorderFactory();
            _recorder = recorder;
            var request = new NativeAudioRecorderRequest(_options.EndpointId, _options.RecordingId, _outputCheck.PartialPath);
            await recorder.InitializeAsync(request, _cts.Token).ConfigureAwait(false);

            _stage = "start";
            var format = await recorder.StartAsync(_outputCheck.PartialPath, _cts.Token).ConfigureAwait(false);
            if (format.SampleRate <= 0 || format.Channels <= 0 || format.BitsPerSample <= 0)
                format = new NativeAudioRecorderFormat(NativeSampleRate, NativeChannels, NativeBitsPerSample);

            _startTimestamp = Stopwatch.GetTimestamp();
            EmitStarted(format);
            _watcher.Start();
            progressTask = RunProgressLoopAsync(format);

            _stage = "recording";
            try
            {
                await recorder.WaitForRecordingFailureAsync(_cts.Token).ConfigureAwait(false);
                throw new NativeAudioRecorderException("audio_native_recording_failed", "MediaCapture recording ended before a stop signal was requested.");
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // Normal stop path.
            }

            _stage = "stop";
            await RunBoundedCleanupAsync("stop", ct => recorder.StopAsync(ct)).ConfigureAwait(false);

            _stage = "finalize";
            var finalized = await RunBoundedCleanupAsync(
                "finalize",
                ct => recorder.FinalizeAsync(_outputCheck.PartialPath, ct)).ConfigureAwait(false);

            _stage = "commit_output";
            if (File.Exists(_outputCheck.CanonicalPath))
                throw new IOException("Output file already exists");
            File.Move(_outputCheck.PartialPath, _outputCheck.CanonicalPath);

            EmitStopped(finalized);
            return ExitOk;
        }
        catch (Exception ex)
        {
            var failureStage = _stage;
            _cts.Cancel();
            await DrainProgressAsync(progressTask).ConfigureAwait(false);
            var secondaryFailure = await CleanupAfterFailureAsync(recorder, failureStage).ConfigureAwait(false);
            EmitFailure(ex, failureStage, secondaryFailure);
            return ExitRuntimeFailure;
        }
        finally
        {
            _cts.Cancel();
            await DrainProgressAsync(progressTask).ConfigureAwait(false);

            try { _watcher.Dispose(); } catch { }
            DisposeRecorder();
        }
    }

    private async Task DrainProgressAsync(Task? progressTask)
    {
        if (progressTask == null)
            return;

        try { await progressTask.WaitAsync(_cleanupTimeout).ConfigureAwait(false); }
        catch { }
    }

    private async Task<T> RunBoundedCleanupAsync<T>(string stage, Func<CancellationToken, Task<T>> operation)
    {
        using var cleanupCts = new CancellationTokenSource(_cleanupTimeout);
        try
        {
            return await operation(cleanupCts.Token).WaitAsync(_cleanupTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new NativeAudioRecorderException(
                $"audio_native_{stage}_failed",
                $"Native {stage} timed out after {_cleanupTimeout.TotalMilliseconds:0} ms.",
                unchecked((int)0x800705B4),
                ex);
        }
        catch (OperationCanceledException ex) when (cleanupCts.IsCancellationRequested)
        {
            throw new NativeAudioRecorderException(
                $"audio_native_{stage}_failed",
                $"Native {stage} was canceled by the bounded cleanup timeout after {_cleanupTimeout.TotalMilliseconds:0} ms.",
                unchecked((int)0x800705B4),
                ex);
        }
    }

    private async Task RunBoundedCleanupAsync(string stage, Func<CancellationToken, Task> operation)
    {
        await RunBoundedCleanupAsync(stage, async ct =>
        {
            await operation(ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private async Task<string> CleanupAfterFailureAsync(INativeAudioRecorder? recorder, string failureStage)
    {
        var failures = new List<string>();
        if (recorder != null && Interlocked.CompareExchange(ref _startedRaised, 0, 0) != 0)
        {
            bool stopSucceeded = failureStage == "finalize";
            if (failureStage is not "stop" and not "finalize")
            {
                try
                {
                    await RunBoundedCleanupAsync("stop", ct => recorder.StopAsync(ct)).ConfigureAwait(false);
                    stopSucceeded = true;
                }
                catch (Exception ex)
                {
                    failures.Add(FormatSecondaryFailure("stop", ex));
                }
            }

            if (stopSucceeded && failureStage is not "stop" and not "finalize")
            {
                try
                {
                    await RunBoundedCleanupAsync("finalize", ct => recorder.FinalizeAsync(_outputCheck.PartialPath, ct)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add(FormatSecondaryFailure("finalize", ex));
                }
            }
        }

        var disposeFailure = DisposeRecorder();
        if (!string.IsNullOrEmpty(disposeFailure))
            failures.Add(disposeFailure);

        return string.Join(" | ", failures);
    }

    private async Task RunProgressLoopAsync(NativeAudioRecorderFormat format)
    {
        while (!_cts.IsCancellationRequested && Interlocked.CompareExchange(ref _terminalRaised, 0, 0) == 0)
        {
            try
            {
                await Task.Delay(ProgressInterval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_cts.IsCancellationRequested || Interlocked.CompareExchange(ref _terminalRaised, 0, 0) != 0)
                break;

            EmitProgress(format);
        }
    }

    private void EmitStarted(NativeAudioRecorderFormat format)
    {
        if (Interlocked.Exchange(ref _startedRaised, 1) != 0)
            return;

        _events.Started(BuildEventInfo(format, durationMs: 0, bytesWritten: GetPartialLength()));
    }

    private void EmitProgress(NativeAudioRecorderFormat format)
    {
        var elapsedMs = ElapsedMsSinceStart();
        _events.Progress(BuildEventInfo(format, durationMs: elapsedMs, bytesWritten: GetPartialLength()));
    }

    private void EmitStopped(NativeAudioRecorderFinalized finalized)
    {
        if (Interlocked.Exchange(ref _terminalRaised, 1) != 0)
            return;

        _events.Stopped(new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            SampleRate = finalized.SampleRate,
            Channels = finalized.Channels,
            BitsPerSample = finalized.BitsPerSample,
            FirstSampleAnchorTicks = _startTimestamp,
            TimestampFrequency = Stopwatch.Frequency,
            DurationMs = finalized.DurationMs,
            WallElapsedMs = finalized.DurationMs,
            BytesWritten = finalized.BytesWritten,
            EstimatedGapMs = 0,
            CaptureMethod = "WINDOWS_MEDIACAPTURE",
            CaptureEngine = AudioCaptureEngineNames.WindowsMediaCapture,
            ContinuityStatus = "ok"
        });
    }

    private void EmitFailure(Exception exception, string failureStage, string secondaryFailure)
    {
        if (Interlocked.Exchange(ref _terminalRaised, 1) != 0)
            return;

        var native = UnwrapNativeException(exception);
        _events.Fail(new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            ErrorCode = native?.ErrorCode ?? ErrorCodeForStage(failureStage, exception),
            Reason = BuildReason(failureStage, exception, native, secondaryFailure, _options.EndpointId),
            BytesWritten = GetPartialLength(),
            EstimatedGapMs = 0,
            CaptureMethod = "WINDOWS_MEDIACAPTURE",
            CaptureEngine = AudioCaptureEngineNames.WindowsMediaCapture,
            Hresult = FormatPrimaryHresult(exception, native),
            FailureStage = failureStage,
            EndpointId = _options.EndpointId,
            PartialOutputPath = _outputCheck.PartialPath,
            SecondaryFailure = secondaryFailure
        });
    }

    private AudioHelperEventInfo BuildEventInfo(NativeAudioRecorderFormat format, long durationMs, long bytesWritten)
    {
        return new AudioHelperEventInfo
        {
            RecordingId = _options.RecordingId,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            BitsPerSample = format.BitsPerSample,
            FirstSampleAnchorTicks = _startTimestamp,
            TimestampFrequency = Stopwatch.Frequency,
            DurationMs = durationMs,
            ElapsedMs = durationMs,
            WallElapsedMs = durationMs,
            BytesWritten = bytesWritten,
            EstimatedGapMs = 0,
            CaptureMethod = "WINDOWS_MEDIACAPTURE",
            CaptureEngine = AudioCaptureEngineNames.WindowsMediaCapture,
            ContinuityStatus = "ok"
        };
    }

    private long ElapsedMsSinceStart()
    {
        if (_startTimestamp <= 0)
            return 0;

        return (long)((Stopwatch.GetTimestamp() - _startTimestamp) * 1000.0 / Stopwatch.Frequency);
    }

    private long GetPartialLength()
    {
        try
        {
            return File.Exists(_outputCheck.PartialPath) ? new FileInfo(_outputCheck.PartialPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static NativeAudioRecorderException? UnwrapNativeException(Exception ex)
    {
        return ex as NativeAudioRecorderException ?? ex.InnerException as NativeAudioRecorderException;
    }

    private static string ErrorCodeForStage(string stage, Exception exception)
    {
        if (exception is OperationCanceledException)
            return "audio_native_stopped_before_started";

        return stage switch
        {
            "initialize" => "audio_native_initialize_failed",
            "start" => "audio_native_start_failed",
            "recording" => "audio_native_recording_failed",
            "stop" => "audio_native_stop_failed",
            "finalize" => "audio_native_finalize_failed",
            "commit_output" => "audio_output_commit_failed",
            "reserve_output" => "audio_output_conflict",
            _ => "audio_native_runtime_failed"
        };
    }

    private static string BuildReason(
        string stage,
        Exception exception,
        NativeAudioRecorderException? native,
        string secondaryFailure,
        string endpointId)
    {
        var hresult = FormatPrimaryHresult(exception, native);
        var reason = $"stage={stage}; endpoint={endpointId}; type={exception.GetType().Name}; hresult={hresult}; sourceEvent={native?.SourceEvent ?? ""}; message={exception.Message}";
        if (!string.IsNullOrEmpty(secondaryFailure))
            reason += $"; secondaryFailure={secondaryFailure}";
        return reason;
    }

    private static string FormatPrimaryHresult(Exception exception, NativeAudioRecorderException? native)
    {
        if (native != null)
            return native.HResultValue.HasValue ? $"0x{native.HResultValue.Value:X8}" : "";

        return $"0x{exception.HResult:X8}";
    }

    private string DisposeRecorder()
    {
        INativeAudioRecorder? recorder;
        lock (_lock)
        {
            recorder = _recorder;
            _recorder = null;
        }

        try
        {
            recorder?.Dispose();
            return "";
        }
        catch (Exception ex)
        {
            return FormatSecondaryFailure("dispose", ex);
        }
    }

    private static string FormatSecondaryFailure(string stage, Exception ex)
    {
        var native = UnwrapNativeException(ex);
        var hresult = native?.HResultValue ?? ex.HResult;
        return $"{stage}:{ex.GetType().Name}:0x{hresult:X8}:{ex.Message}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _cts.Cancel(); } catch { }
        try { _watcher.Dispose(); } catch { }

        var runTask = _runTask;
        if (runTask == null || runTask.IsCompleted)
            _ = DisposeRecorder();
    }
}
