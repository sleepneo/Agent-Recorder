using System.Runtime.InteropServices;

namespace AgentRecorder.AudioHelper;

/// <summary>
/// Structured diagnostics for a single failed capture stage. Used to retain a
/// secondary failure (e.g. ReleaseBuffer failing while a ReadPacket error is
/// already the primary root cause) in a typed, testable form instead of an
/// opaque free-text blob.
/// </summary>
internal sealed class AudioCaptureFailureInfo
{
    public string Stage { get; }
    public int Hresult { get; }
    public string ExceptionType { get; }
    public string FailureMessage { get; }

    public AudioCaptureFailureInfo(string stage, int hresult, string exceptionType, string failureMessage)
    {
        Stage = stage;
        Hresult = hresult;
        ExceptionType = exceptionType;
        FailureMessage = failureMessage;
    }

    public override string ToString()
        => $"{Stage} failed ({ExceptionType}, HRESULT=0x{Hresult:X8}): {FailureMessage}";
}

/// <summary>
/// Exception thrown when a WASAPI capture operation fails after the stream has
/// been started. Carries the failing stage and original HRESULT so the caller
/// can emit a stable error code and retain diagnostics.
/// </summary>
internal sealed class AudioCaptureRuntimeException : Exception
{
    private AudioCaptureFailureInfo? _secondaryFailure;

    public string Stage { get; }
    public int Hresult { get; }
    public string? ErrorCode { get; }

    /// <summary>
    /// Diagnostics of a secondary failure that occurred while the primary
    /// failure (kept in <see cref="Stage"/>/<see cref="Hresult"/>) was being
    /// unwound. Null when no secondary failure was observed.
    /// </summary>
    public AudioCaptureFailureInfo? SecondaryFailure => _secondaryFailure;

    /// <summary>
    /// Primary failure message; when a secondary failure is attached its
    /// diagnostics are appended so consumers that only read Message (e.g. the
    /// helper terminal event reason) still surface both failures.
    /// </summary>
    public override string Message
    {
        get
        {
            var secondary = _secondaryFailure;
            return secondary == null
                ? base.Message
                : base.Message + "; secondary failure: " + secondary;
        }
    }

    public AudioCaptureRuntimeException(string stage, string message, Exception innerException, int hresult, string? errorCode = null)
        : base(message, innerException)
    {
        Stage = stage;
        Hresult = hresult;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Attaches a secondary failure observed while the primary failure was
    /// being handled. The first attachment wins so primary/secondary ordering
    /// stays deterministic; the primary stage and HRESULT are never modified.
    /// Returns true when this call attached the secondary failure.
    /// </summary>
    public bool TryAttachSecondaryFailure(string stage, Exception ex)
    {
        var info = new AudioCaptureFailureInfo(stage, HresultFrom(ex), ex.GetType().Name, ex.Message);
        return Interlocked.CompareExchange(ref _secondaryFailure, info, null) == null;
    }

    internal static AudioCaptureRuntimeException FromException(string stage, Exception ex)
    {
        // Preserve an already-classified runtime exception so the original
        // stage and HRESULT are not overwritten by the outer catch handler.
        if (ex is AudioCaptureRuntimeException runtimeEx)
            return runtimeEx;

        int hresult = HresultFrom(ex);

        return new AudioCaptureRuntimeException(
            stage,
            $"{stage} failed ({ex.GetType().Name}, HRESULT=0x{hresult:X8}): {ex.Message}",
            ex,
            hresult);
    }

    private static int HresultFrom(Exception ex)
    {
        if (ex is COMException comEx)
            return comEx.HResult;
        try
        {
            return ex.HResult;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Exception thrown when <see cref="AudioClientAudioInput.StartRecording"/> fails.
/// Carries the original HRESULT so the caller can emit a stable error code.
/// </summary>
internal sealed class AudioCaptureStartException : Exception
{
    public int Hresult { get; }
    public string? ErrorCode { get; }
    public string Stage { get; }

    public AudioCaptureStartException(string message, Exception innerException, int hresult,
        string? errorCode = null, string stage = "AudioCaptureStart")
        : base(message, innerException)
    {
        Hresult = hresult;
        ErrorCode = errorCode;
        Stage = stage;
    }
}
