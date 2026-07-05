using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Logging;

namespace AgentRecorder.Capture;

public enum PrewarmStatus
{
    NotStarted,
    Running,
    Completed,
    Failed,
    Skipped
}

public sealed class FfmpegPrewarmResult
{
    public PrewarmStatus Status { get; set; }
    public long ElapsedMs { get; set; }
    public bool FfmpegFound { get; set; }
    public bool FfprobeFound { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Background FFmpeg/FFprobe prewarmer. Runs after service readiness is marked
/// so that the first recording does not pay cold-start process-spawn latency.
/// Runs only once; failures are non-fatal and logged as audit events.
/// </summary>
public sealed class FfmpegPrewarmer
{
    private const int DefaultTimeoutMs = 3000;
    private readonly object _lock = new();
    private Task? _task;
    private PrewarmStatus _status = PrewarmStatus.NotStarted;
    private long _elapsedMs;
    private bool _ffmpegFound;
    private bool _ffprobeFound;
    private string? _errorCode;
    private string? _errorMessage;
    private int _startCount;

    /// <summary>
    /// Injectable version-check runner for testing. Defaults to the real process-based check.
    /// </summary>
    internal Func<string, int, bool> VersionCheckRunner { get; set; } = RunVersionCheck;

    /// <summary>
    /// Number of times Start() actually began a prewarm task. For testing only.
    /// </summary>
    public int StartCount
    {
        get { lock (_lock) return _startCount; }
    }

    public PrewarmStatus Status
    {
        get { lock (_lock) return _status; }
    }

    public FfmpegPrewarmResult CurrentResult
    {
        get
        {
            lock (_lock)
            {
                return new FfmpegPrewarmResult
                {
                    Status = _status,
                    ElapsedMs = _elapsedMs,
                    FfmpegFound = _ffmpegFound,
                    FfprobeFound = _ffprobeFound,
                    ErrorCode = _errorCode,
                    ErrorMessage = _errorMessage
                };
            }
        }
    }

    /// <summary>
    /// Start the prewarm in a background thread. Safe to call multiple times;
    /// only the first call has effect. Never throws.
    /// If audit is null, a no-op logger is used internally.
    /// </summary>
    public void Start(AuditLogger? audit)
    {
        lock (_lock)
        {
            if (_task != null) return;
            _status = PrewarmStatus.Running;
            _startCount++;
            var safeAudit = audit ?? NoOpAuditLogger.Instance;
            _task = Task.Run(() => RunPrewarm(safeAudit));
        }
    }

    private void RunPrewarm(AuditLogger audit)
    {
        var sw = Stopwatch.StartNew();
        string? ffmpegPath = null;
        string? ffprobePath = null;
        bool ffmpegOk = false;
        bool ffprobeOk = false;
        string? errorCode = null;
        string? errorMsg = null;

        try
        {
            audit.Log("ffmpeg.prewarm_started", new { source = FfmpegLocator.Source });

            try
            {
                ffmpegPath = FfmpegLocator.FfmpegPath;
                _ffmpegFound = true;
                ffmpegOk = VersionCheckRunner(ffmpegPath, DefaultTimeoutMs);
            }
            catch (Exception ex)
            {
                _ffmpegFound = false;
                errorCode = "ffmpeg_not_found";
                errorMsg = ex.Message;
            }

            try
            {
                ffprobePath = FfmpegLocator.FfprobePath;
                _ffprobeFound = true;
                ffprobeOk = VersionCheckRunner(ffprobePath, DefaultTimeoutMs);
            }
            catch (Exception ex)
            {
                _ffprobeFound = false;
                if (errorCode == null)
                {
                    errorCode = "ffprobe_not_found";
                    errorMsg = ex.Message;
                }
            }

            bool allOk = ffmpegOk && ffprobeOk;

            lock (_lock)
            {
                sw.Stop();
                _elapsedMs = sw.ElapsedMilliseconds;
                if (allOk)
                {
                    _status = PrewarmStatus.Completed;
                }
                else if (!_ffmpegFound && !_ffprobeFound)
                {
                    _status = PrewarmStatus.Skipped;
                    if (errorCode == null) { errorCode = "not_found"; errorMsg = "Neither ffmpeg nor ffprobe found."; }
                }
                else
                {
                    _status = PrewarmStatus.Failed;
                    if (errorCode == null) { errorCode = "version_check_failed"; errorMsg = "One or more version checks failed."; }
                }
                _errorCode = errorCode;
                _errorMessage = errorMsg;
            }

            var result = CurrentResult;
            audit.Log("ffmpeg.prewarm_completed", new
            {
                elapsed_ms = result.ElapsedMs,
                source = FfmpegLocator.Source,
                ffmpeg_found = result.FfmpegFound,
                ffprobe_found = result.FfprobeFound,
                ffmpeg_ok = ffmpegOk,
                ffprobe_ok = ffprobeOk,
                status = result.Status.ToString().ToLowerInvariant()
            });
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                sw.Stop();
                _elapsedMs = sw.ElapsedMilliseconds;
                _status = PrewarmStatus.Failed;
                _errorCode = "prewarm_exception";
                _errorMessage = ex.Message;
            }

            try
            {
                var result = CurrentResult;
                audit.Log("ffmpeg.prewarm_failed", new
                {
                    elapsed_ms = result.ElapsedMs,
                    source = FfmpegLocator.Source,
                    ffmpeg_found = result.FfmpegFound,
                    ffprobe_found = result.FfprobeFound,
                    error_code = result.ErrorCode,
                    error = result.ErrorMessage
                });
            }
            catch { }
        }
    }

    internal static bool RunVersionCheck(string exePath, int timeoutMs)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-hide_banner -version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            if (!proc.Start())
                return false;

            // Consume output (truncated, we only care about exit code)
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// No-op audit logger for testing and as null-safety fallback.
/// </summary>
internal sealed class NoOpAuditLogger : AuditLogger
{
    public static readonly NoOpAuditLogger Instance = new();
    private NoOpAuditLogger() { }
    public override void Log(string evt, object payload) { }
}
