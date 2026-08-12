using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// WASAPI capture input built directly on the CoreAudio seam. The AudioClient
/// is initialized but not started when this object is constructed;
/// <see cref="StartRecording"/> starts the AudioClient and the capture thread,
/// and <see cref="StopRecording"/> requests a graceful stop. All COM objects
/// are owned by this instance and released on disposal.
/// </summary>
internal sealed class AudioClientAudioInput : IAudioInput, IAudioPacketPositionSource
{
    private const long ReftimesPerSec = 10000000L;
    private const long ReftimesPerMillisec = 10000L;
    private static readonly TimeSpan DefaultThreadJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly IDevice _device;
    private readonly IAudioClient _audioClient;
    private readonly IAudioCaptureClient _captureClient;
    private readonly WaveFormat _waveFormat;
    private readonly AudioSourceKind _sourceKind;
    private readonly int _bufferMilliseconds;
    private readonly SynchronizationContext? _syncContext;
    private readonly TimeSpan _joinTimeout;
    private readonly EventWaitHandle? _captureEvent;

    private byte[] _recordBuffer;
    private readonly int _bytesPerFrame;
    private readonly int _sleepMilliseconds;

    private Thread? _captureThread;
    private readonly ManualResetEventSlim _threadExited = new(false);
    private readonly ManualResetEventSlim _startCompleted = new(false);
    private int _state;
    private volatile bool _stopRequested;
    private int _recordingStoppedRaised;
    private int _disposeCompleted;
    private int _cleanupOwner;
    private int _captureThreadStarted;
    private int _resourcesReleased;
    private long _discontinuityCount;

    public WaveFormat? Format => _waveFormat;

    public AudioSourceKind SourceKind => _sourceKind;

    /// <summary>
    /// Number of packets that arrived with the WASAPI DataDiscontinuity flag.
    /// Read-only, thread-safe; never reset over the input's lifetime.
    /// </summary>
    public long DiscontinuityCount => Interlocked.Read(ref _discontinuityCount);

    /// <summary>
    /// True once the COM objects have been released exactly once and the
    /// disposal sequence is fully complete. Exposed for deterministic
    /// concurrency tests.
    /// </summary>
    public bool DisposeCompletedSuccessfully => _disposeCompleted == 1;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<AudioPacketEventArgs>? PacketPositionAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    private enum State
    {
        Created = 0,
        Starting = 1,
        Capturing = 2,
        Stopping = 3,
        Stopped = 4,
        Disposed = 5
    }

    public AudioClientAudioInput(
        IDevice device,
        IAudioClient audioClient,
        IAudioCaptureClient captureClient,
        WaveFormat waveFormat,
        int bufferMilliseconds,
        TimeSpan? joinTimeout = null,
        bool eventDriven = false,
        AudioSourceKind sourceKind = AudioSourceKind.Microphone)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _audioClient = audioClient ?? throw new ArgumentNullException(nameof(audioClient));
        _captureClient = captureClient ?? throw new ArgumentNullException(nameof(captureClient));
        _waveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
        _sourceKind = sourceKind;
        _bufferMilliseconds = bufferMilliseconds;
        _syncContext = SynchronizationContext.Current;
        _joinTimeout = joinTimeout ?? DefaultThreadJoinTimeout;
        if (eventDriven)
        {
            if (audioClient is not IEventDrivenAudioClient)
                throw new ArgumentException("Event-driven capture requires an event-capable AudioClient", nameof(audioClient));
        }

        int bufferFrameCount = audioClient.BufferSize;
        _bytesPerFrame = waveFormat.Channels * waveFormat.BitsPerSample / 8;
        if (_bytesPerFrame <= 0)
            throw new ArgumentException("Wave format yields zero bytes per frame", nameof(waveFormat));
        if (waveFormat.SampleRate <= 0)
            throw new ArgumentException("Wave format has an invalid sample rate", nameof(waveFormat));

        _recordBuffer = new byte[Math.Max(1, bufferFrameCount) * _bytesPerFrame];

        long actualDuration = (long)((double)ReftimesPerSec * bufferFrameCount / waveFormat.SampleRate);
        _sleepMilliseconds = Math.Max(1, (int)(actualDuration / ReftimesPerMillisec / 2));

        // Allocate the handle only after all validation, native property reads,
        // and managed buffer allocation that can fail. A constructor failure
        // therefore cannot strand a newly-created event handle.
        if (eventDriven)
            _captureEvent = new AutoResetEvent(false);
    }

    public StartRecordingResult StartRecording()
    {
        // Only one thread may leave Created.
        int previous = Interlocked.CompareExchange(ref _state, (int)State.Starting, (int)State.Created);
        if (previous == (int)State.Disposed)
            return StartRecordingResult.Disposed;
        if (previous != (int)State.Created)
            throw new InvalidOperationException("Recording has already been started");

        bool audioClientStarted = false;
        try
        {
            // Start the AudioClient first. This is where AirPods/HFP endpoints
            // have historically failed with E_INVALIDARG; by performing it here
            // (rather than during construction) the failure can be retried by
            // the caller with a fresh device/client/format negotiation.
            if (_captureEvent != null)
            {
                ((IEventDrivenAudioClient)_audioClient).SetEventHandle(_captureEvent.SafeWaitHandle.DangerousGetHandle());
            }
            _audioClient.Start();
            audioClientStarted = true;

            // If Dispose raced in and moved us to Disposed while Start was in
            // flight, stop the now-started client and exit without raising
            // RecordingStopped (the input never actually began capturing).
            int current = _state;
            if (current == (int)State.Disposed)
            {
                StopAudioClient();
                return StartRecordingResult.Disposed;
            }

            // If StopRecording raced in and moved us to Stopping, stop the
            // client and report a single stopped event for the user-requested
            // abort.
            if (current != (int)State.Starting)
            {
                StopAudioClient();
                TransitionToStopped(null);
                return StartRecordingResult.Cancelled;
            }

            var thread = new Thread(CaptureThread)
            {
                IsBackground = true,
                Name = "AudioClientCapture"
            };

            if (Interlocked.CompareExchange(ref _captureThread, thread, null) != null)
            {
                // Should never happen because of the state guard, but be defensive.
                StopAudioClient();
                TransitionToStopped(null);
                throw new InvalidOperationException("Capture thread already exists");
            }

            // If StopRecording raced in and set _stopRequested before we created
            // the thread, do not start an unneeded thread. Stop the AudioClient
            // and report a stopped event.
            if (_stopRequested)
            {
                StopAudioClientAndTransitionToStopped();
                return StartRecordingResult.Cancelled;
            }

            // Publish Capturing before starting the thread so the thread sees a
            // consistent state immediately on entry.
            if (Interlocked.CompareExchange(ref _state, (int)State.Capturing, (int)State.Starting) != (int)State.Starting)
            {
                // A concurrent Stop/Dispose already moved us out of Starting.
                StopAudioClientAndTransitionToStopped();
                return StartRecordingResult.Cancelled;
            }

            thread.Start();
            Interlocked.Exchange(ref _captureThreadStarted, 1);
            return StartRecordingResult.Started;
        }
        catch (Exception ex)
        {
            if (_state == (int)State.Disposed)
            {
                // Dispose won while we were inside the COM call. Do not raise
                // RecordingStopped and do not report a retryable error.
                StopAudioClient();
                return StartRecordingResult.Disposed;
            }

            if (audioClientStarted)
            {
                // Start succeeded but something else failed afterwards. The
                // stream did begin, so report a stopped event and rethrow the
                // original exception.
                StopAudioClientAndTransitionToStopped();
                throw;
            }

            // Start never succeeded. Roll back to Created so the caller can
            // retry with a fresh input, but do not emit RecordingStopped.
            StopAudioClient();
            Interlocked.CompareExchange(ref _state, (int)State.Created, (int)State.Starting);
            var hresult = HresultFrom(ex);
            throw new AudioCaptureStartException(
                $"StartRecording failed ({ex.GetType().Name}, HRESULT={FormatHresult(hresult)}): {ex.Message}",
                ex,
                hresult);
        }
        finally
        {
            // Signal anyone waiting in Dispose. Swallow ObjectDisposedException
            // in case Dispose already timed out and disposed the event.
            try { _startCompleted.Set(); }
            catch (ObjectDisposedException) { }

            // If Dispose won and we never started a capture thread, we are the
            // last owner that can safely release the COM objects. The capture
            // thread, when it exists, handles cleanup via its own finally.
            if (_captureThreadStarted == 0 && _state == (int)State.Disposed)
            {
                int owner = Interlocked.CompareExchange(ref _cleanupOwner, 2, 0);
                if (owner == 0 || owner == 2)
                {
                    StopAudioClient();
                    if (Interlocked.Exchange(ref _resourcesReleased, 1) == 0)
                    {
                        ReleaseComObjects();
                    }
                    // If Dispose timed out and delegated cleanup to us, mark it
                    // as completed now that resources are released.
                    Interlocked.CompareExchange(ref _disposeCompleted, 1, -1);
                }
            }
        }
    }

    public void StopRecording()
    {
        _stopRequested = true;

        while (true)
        {
            int current = _state;
            switch ((State)current)
            {
                case State.Created:
                    // StartRecording has not been called yet. Move directly to
                    // Stopped without starting anything.
                    if (Interlocked.CompareExchange(ref _state, (int)State.Stopped, (int)State.Created) == (int)State.Created)
                    {
                        StopAudioClient();
                        RaiseRecordingStopped(null);
                        return;
                    }
                    break;

                case State.Starting:
                    // StartRecording is in progress. Move to Stopping so that
                    // StartRecording sees the request and does not start the
                    // capture thread.
                    if (Interlocked.CompareExchange(ref _state, (int)State.Stopping, (int)State.Starting) == (int)State.Starting)
                        return;
                    break;

                case State.Capturing:
                    // Thread is running; it will observe _stopRequested and exit.
                    return;

                case State.Stopping:
                case State.Stopped:
                case State.Disposed:
                    return;
            }
        }
    }

    public void Dispose()
    {
        // Dispose is idempotent and must complete even if the capture thread is
        // stuck. The strategy is:
        // 1. Request stop and atomically move to Disposed, capturing the state
        //    *before* the transition.
        // 2. Use that captured previous state (not the current Disposed state)
        //    to decide what may still be running.
        // 3. If StartRecording was in progress, wait for it to leave its
        //    critical section. If it does not leave in time, transfer cleanup
        //    ownership to Start's finally and return without releasing objects.
        // 4. Join the capture thread with a bounded timeout if it may be running.
        // 5. Use atomic cleanup ownership so COM objects are released exactly
        //    once: by Dispose if it joined successfully, otherwise by the
        //    capture thread or Start finally when they eventually exit.

        _stopRequested = true;

        int previous;
        while (true)
        {
            int current = _state;
            if (current == (int)State.Disposed)
                return;

            if (Interlocked.CompareExchange(ref _state, (int)State.Disposed, current) == current)
            {
                previous = current;
                break;
            }
        }

        bool startCompleted = true;

        // If StartRecording is in progress, wait for it to finish deciding the
        // fate of the AudioClient. A bounded wait is used; if Start is stuck
        // inside a synchronous COM call, we return without releasing objects
        // and let Start's finally complete cleanup.
        if (previous == (int)State.Starting)
        {
            startCompleted = _startCompleted.Wait(_joinTimeout);
            if (!startCompleted)
            {
                // Start is still inside _audioClient.Start() or immediately
                // after. Transfer cleanup ownership to Start's finally and exit
                // without disposing the events (Start will Set them).
                Interlocked.CompareExchange(ref _cleanupOwner, 2, 0);
                _disposeCompleted = -1;
                return;
            }
        }

        // Use the captured previous state, not _state (which is now Disposed).
        // - Created/Stopped: no thread is running.
        // - Starting (after wait succeeded): Start has finished without starting
        //   a thread, or _captureThread was created but not started.
        // - Capturing/Stopping: capture thread may be running; join first.
        bool threadMayBeRunning = previous == (int)State.Capturing || previous == (int)State.Stopping;
        bool threadExited = true;

        if (threadMayBeRunning)
        {
            var thread = Interlocked.Exchange(ref _captureThread, null);
            if (thread != null)
            {
                threadExited = _threadExited.Wait(_joinTimeout);
            }
        }
        else
        {
            // Discard any thread object that was created but never started.
            Interlocked.Exchange(ref _captureThread, null);
        }

        // Exactly-once cleanup ownership:
        // 0 = undecided, 1 = Dispose owns cleanup, 2 = capture thread/Start owns cleanup.
        // _resourcesReleased is the independent physical gate that guarantees
        // ReleaseComObjects is invoked at most once, regardless of cleanup owner.
        bool disposeOwnsCleanup = false;
        if (threadExited)
        {
            disposeOwnsCleanup = Interlocked.CompareExchange(ref _cleanupOwner, 1, 0) == 0;
        }

        if (disposeOwnsCleanup)
        {
            if (Interlocked.Exchange(ref _resourcesReleased, 1) == 0)
            {
                ReleaseComObjects();
            }
            _disposeCompleted = 1;
        }
        else if (!threadExited)
        {
            Interlocked.CompareExchange(ref _cleanupOwner, 2, 0);
            _disposeCompleted = -1;
        }

        try { _startCompleted.Dispose(); } catch { }
        if (threadExited)
        {
            try { _threadExited.Dispose(); } catch { }
        }
    }

    private void CaptureThread()
    {
        AudioCaptureRuntimeException? runtimeException = null;
        try
        {
            while (!_stopRequested)
            {
                int packetSize;
                try
                {
                    packetSize = _captureClient.GetNextPacketSize();
                }
                catch (Exception ex)
                {
                    runtimeException = AudioCaptureRuntimeException.FromException("GetNextPacketSize", ex);
                    break;
                }

                if (packetSize == 0)
                {
                    if (_captureEvent != null)
                        _captureEvent.WaitOne(_sleepMilliseconds);
                    else
                        Thread.Sleep(_sleepMilliseconds);
                    continue;
                }

                IntPtr buffer;
                int frames;
                AudioClientBufferFlags flags;
                long devicePosition;
                long qpcPosition;
                try
                {
                    buffer = _captureClient.GetBuffer(out frames, out flags, out devicePosition, out qpcPosition);
                }
                catch (Exception ex)
                {
                    runtimeException = AudioCaptureRuntimeException.FromException("GetBuffer", ex);
                    break;
                }

                if ((flags & AudioClientBufferFlags.DataDiscontinuity) == AudioClientBufferFlags.DataDiscontinuity)
                    Interlocked.Increment(ref _discontinuityCount);

                try
                {
                    int bytesAvailable = checked(frames * _bytesPerFrame);
                    if (bytesAvailable > 0)
                        ReadPacket(buffer, bytesAvailable, frames, flags, devicePosition, qpcPosition);
                }
                catch (Exception ex)
                {
                    runtimeException = AudioCaptureRuntimeException.FromException("ReadPacket", ex);
                }
                finally
                {
                    try
                    {
                        _captureClient.ReleaseBuffer(frames);
                    }
                    catch (Exception ex)
                    {
                        if (runtimeException == null)
                        {
                            runtimeException = AudioCaptureRuntimeException.FromException("ReleaseBuffer", ex);
                        }
                        else
                        {
                            // The packet-processing error keeps root-cause
                            // priority; retain the ReleaseBuffer failure as
                            // structured secondary diagnostics instead of
                            // dropping it.
                            runtimeException.TryAttachSecondaryFailure("ReleaseBuffer", ex);
                        }
                    }
                }

                if (runtimeException != null)
                    break;
            }
        }
        catch (Exception ex)
        {
            runtimeException ??= AudioCaptureRuntimeException.FromException("CaptureThread", ex);
        }
        finally
        {
            var stopFailure = StopAudioClient();
            if (stopFailure != null)
            {
                if (runtimeException == null)
                    runtimeException = stopFailure;
                else
                    runtimeException.TryAttachSecondaryFailure(stopFailure.Stage, stopFailure);
            }

            TransitionToStopped(runtimeException);

            if (Interlocked.Exchange(ref _resourcesReleased, 1) == 0)
            {
                int owner = Interlocked.CompareExchange(ref _cleanupOwner, 2, 0);
                if (owner == 0 || owner == 2)
                {
                    ReleaseComObjects();
                    Interlocked.Exchange(ref _disposeCompleted, 1);
                }
            }

            try { _threadExited.Set(); } catch (ObjectDisposedException) { }
        }
    }

    private void ReadPacket(
        IntPtr source,
        int bytesAvailable,
        int framesAvailable,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        bool silent = (flags & AudioClientBufferFlags.Silent) == AudioClientBufferFlags.Silent;
        bool positionValid = devicePosition >= 0 && qpcPosition > 0 &&
                             (flags & AudioClientBufferFlags.TimestampError) == 0;
        long callbackTimestamp = Stopwatch.GetTimestamp();
        long packetStartTimestamp = positionValid
            ? AudioPacketPositionMath.ConvertQpcToStopwatchTicks(
                qpcPosition, callbackTimestamp, Stopwatch.Frequency)
            : 0;

        if (silent)
        {
            // Silent packets contain equal-length zero samples regardless of the
            // data pointer. Split large silent packets into bounded chunks so a
            // single abnormal packet cannot allocate an unbounded buffer.
            int silentOffset = 0;
            while (silentOffset < bytesAvailable)
            {
                int chunkSize = Math.Min(_recordBuffer.Length, bytesAvailable - silentOffset);
                Array.Fill(_recordBuffer, (byte)0, 0, chunkSize);
                PublishPacket(
                    _recordBuffer,
                    chunkSize,
                    framesAvailable,
                    silentOffset,
                    devicePosition,
                    qpcPosition,
                    packetStartTimestamp,
                    positionValid);
                silentOffset += chunkSize;
            }
            return;
        }

        if (source == IntPtr.Zero)
        {
            throw new AudioCaptureRuntimeException(
                "ReadPacket",
                "ReadPacket failed (InvalidOperationException, HRESULT=0x80004003): Non-silent packet has a null buffer pointer",
                new InvalidOperationException("Buffer pointer is zero for a non-silent packet"),
                unchecked((int)0x80004003)); // E_POINTER
        }

        int readOffset = 0;
        while (readOffset < bytesAvailable)
        {
            int chunkBytes = Math.Min(_recordBuffer.Length, bytesAvailable - readOffset);
            Marshal.Copy(source + readOffset, _recordBuffer, 0, chunkBytes);
            PublishPacket(
                _recordBuffer,
                chunkBytes,
                framesAvailable,
                readOffset,
                devicePosition,
                qpcPosition,
                packetStartTimestamp,
                positionValid);
            readOffset += chunkBytes;
        }
    }

    private void PublishPacket(
        byte[] buffer,
        int bytesRecorded,
        int framesAvailable,
        int byteOffset,
        long devicePosition,
        long qpcPosition,
        long packetStartTimestamp,
        bool positionValid)
    {
        int framesOffset = byteOffset / _bytesPerFrame;
        int framesRecorded = bytesRecorded / _bytesPerFrame;
        long chunkDevicePosition = devicePosition >= 0
            ? checked(devicePosition + framesOffset)
            : -1;
        long chunkQpcPosition = qpcPosition > 0 && _waveFormat.SampleRate > 0
            ? checked(qpcPosition + AudioPacketPositionMath.FramesToTimestampTicks(
                framesOffset, _waveFormat.SampleRate, AudioPacketPositionMath.QpcUnitsPerSecond))
            : 0;
        long chunkTimestamp = packetStartTimestamp > 0 && _waveFormat.SampleRate > 0
            ? checked(packetStartTimestamp + AudioPacketPositionMath.FramesToTimestampTicks(
                framesOffset, _waveFormat.SampleRate, Stopwatch.Frequency))
            : 0;

        PacketPositionAvailable?.Invoke(this, new AudioPacketEventArgs(
            buffer,
            bytesRecorded,
            framesRecorded,
            chunkDevicePosition,
            chunkQpcPosition,
            chunkTimestamp,
            positionValid));
        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytesRecorded));
    }

    private AudioCaptureRuntimeException? StopAudioClient()
    {
        try { _captureEvent?.Set(); } catch { }
        try
        {
            _audioClient.Stop();
        }
        catch (Exception ex)
        {
            return AudioCaptureRuntimeException.FromException("Stop", ex);
        }

        return null;
    }

    private void StopAudioClientAndTransitionToStopped()
    {
        StopAudioClient();
        TransitionToStopped(null);
    }

    private void TransitionToStopped(Exception? exception)
    {
        // Move to Stopped unless we are already Disposed or Stopped. Never
        // raise RecordingStopped for a Disposed input.
        while (true)
        {
            int current = _state;
            if (current == (int)State.Stopped || current == (int)State.Disposed)
                return;

            if (Interlocked.CompareExchange(ref _state, (int)State.Stopped, current) == current)
                break;
        }

        RaiseRecordingStopped(exception);
    }

    private void RaiseRecordingStopped(Exception? exception)
    {
        if (Interlocked.Exchange(ref _recordingStoppedRaised, 1) == 0)
        {
            var handler = RecordingStopped;
            if (handler == null)
                return;

            var args = new StoppedEventArgs(exception);
            if (_syncContext != null)
            {
                _syncContext.Post(_ => handler(this, args), null);
            }
            else
            {
                handler(this, args);
            }
        }
    }

    private void ReleaseComObjects()
    {
        try { _captureClient.Dispose(); } catch { }
        try { _audioClient.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
        try { _captureEvent?.Dispose(); } catch { }
    }

    private static int HresultFrom(Exception ex)
    {
        if (ex is COMException comEx)
            return comEx.HResult;
        try { return ex.HResult; }
        catch { return 0; }
    }

    private static string FormatHresult(int hresult)
    {
        if (hresult == 0)
            return "0x00000000";
        return $"0x{hresult:X8}";
    }
}
