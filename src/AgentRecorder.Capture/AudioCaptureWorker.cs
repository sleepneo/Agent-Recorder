using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Logging;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Capture;

/// <summary>
/// Captures a single dshow microphone input to a temporary WAV file.
/// Exposes a readiness signal once the output file contains credible audio
/// samples, so callers can wait before starting screen capture.
/// </summary>
public sealed class AudioCaptureWorker : IAudioCaptureWorker
{
    private Process? _proc;
    private readonly StringBuilder _stderrLog = new();
    private readonly object _lock = new();
    private Task? _stdoutReader;
    private Task? _watcher;
    private TaskCompletionSource<bool>? _exitTcs;
    private long _readyRaised;
    private long _mediaStartAnchorTicks;
    private bool _hasExited;
    private bool _manualStopped;
    private ManualResetEventSlim? _stderrClosed;
    private long _runtimeAudioLostAtMs;
    private IMicrophoneStatusProvider? _microphoneStatusProvider;
    private CancellationTokenSource? _microphoneMonitorCts;
    private Task? _microphoneMonitorTask;
    private readonly IExternalProcessRunner? _runner;

    private static readonly TimeSpan StdoutDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StderrDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KillDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MicrophoneMonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MicrophoneMonitorShutdownTimeout = TimeSpan.FromSeconds(3);
    internal const string TimestampCompensationFilter = "aresample=async=1:first_pts=0";

    /// <summary>
    /// Test seam: when set, Start uses these arguments instead of the real
    /// dshow arguments. Allows unit tests to exercise lifecycle with a
    /// short-lived, non-recording FFmpeg invocation such as "-version".
    /// </summary>
    internal List<string>? TestArgumentsOverride { get; set; }

    public event Action? AudioReady;
    public event Action<int, string>? NaturalExit;

    public AudioCaptureWorker(IExternalProcessRunner? runner = null)
    {
        _runner = runner;
    }

    /// <summary>
    /// Best-effort wall-clock timestamp when readiness was first observed.
    /// This is the notification time, NOT the media timestamp of the first sample.
    /// </summary>
    public DateTime? ReadyAtUtc { get; private set; }

    /// <summary>
    /// Best-effort monotonic-clock estimate (Stopwatch.GetTimestamp ticks) of
    /// the first audio sample's media time zero.
    /// </summary>
    public long MediaStartAnchorTicks => Interlocked.Read(ref _mediaStartAnchorTicks);

    public bool IsAudioReady => ReadyAtUtc.HasValue;

    /// <summary>
    /// The output path the worker was configured to write.
    /// </summary>
    public string? OutputPath { get; private set; }

    public int ExitCode { get; private set; } = -1;

    public bool HasExited
    {
        get
        {
            lock (_lock) return _hasExited;
        }
    }

    public void SetMicrophoneStatusProvider(IMicrophoneStatusProvider? provider)
    {
        _microphoneStatusProvider = provider;
    }

    public void Start(CaptureConfig cfg, string outputPath)
    {
        if (TestArgumentsOverride == null && string.IsNullOrEmpty(cfg.MicDevice))
            throw new ArgumentException("Microphone device is required for audio worker", nameof(cfg));

        OutputPath = outputPath;
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var args = TestArgumentsOverride ?? BuildArgs(cfg, outputPath);
        var parser = new FFmpegProgressParser();
        parser.GroupCompleted += g =>
        {
            if (g.OutTimeUs <= 0) return;
            if (Interlocked.Exchange(ref _readyRaised, 1) != 0) return;

            // Estimate the media-time zero of the audio stream only when the
            // progress carries a credible positive out_time_us. Otherwise keep
            // the anchor at zero so the finalizer sees it as missing.
            if (MediaAnchorHelper.TryEstimateMediaStartAnchor(Stopwatch.GetTimestamp(), g.OutTimeUs, out var anchorTicks))
                Interlocked.Exchange(ref _mediaStartAnchorTicks, anchorTicks);
            else
                Interlocked.Exchange(ref _mediaStartAnchorTicks, 0);

            ReadyAtUtc = DateTime.UtcNow;
            try { AudioReady?.Invoke(); }
            catch { }
        };

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FfmpegLocator.FfmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        foreach (var a in args)
            _proc.StartInfo.ArgumentList.Add(a);

        _stderrClosed = new ManualResetEventSlim();
        _exitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                _stderrClosed?.Set();
                return;
            }
            if (!string.IsNullOrWhiteSpace(e.Data))
                lock (_lock) _stderrLog.AppendLine(e.Data);
        };

        try { _proc.Start(); }
        catch (Exception ex)
        {
            _stderrClosed?.Set();
            _exitTcs?.TrySetResult(true);
            throw new ApiException(500, "ENCODER_ERROR", "Failed to launch audio worker: " + ex.Message);
        }
        _proc.BeginErrorReadLine();
        _stdoutReader = RunStdoutReader(_proc.StandardOutput, parser);

        StartMicrophoneMonitor(cfg);

        int timeoutMs = (cfg.DurationSeconds.HasValue && cfg.DurationSeconds > 0)
            ? (cfg.DurationSeconds.Value + 60) * 1000
            : 4 * 3600 * 1000;

        _watcher = Task.Run(() =>
        {
            bool exited = _proc.WaitForExit(timeoutMs);
            int exitCode = -1;
            try { if (exited) exitCode = _proc.ExitCode; } catch { }
            if (!exited)
            {
                try { _proc.Kill(true); } catch { }
                // Give the killed process tree a bounded window to actually exit
                // before publishing completion, so HasExited/ExitCode reflect the
                // real termination.
                try { exited = _proc.WaitForExit(KillDrainTimeout); } catch { }
                try { if (exited) exitCode = _proc.ExitCode; } catch { }
            }

            DrainTask(_stdoutReader, StdoutDrainTimeout);
            WaitStderrClosed(StderrDrainTimeout);
            StopMicrophoneMonitor();
            string stderr;
            lock (_lock)
            {
                _hasExited = true;
                ExitCode = exitCode;
                stderr = _stderrLog.ToString();
            }
            _exitTcs?.TrySetResult(true);
            if (!_manualStopped)
            {
                try { NaturalExit?.Invoke(exitCode, stderr); }
                catch { }
            }
        });
    }

    public void Stop()
    {
        lock (_lock) _manualStopped = true;
        if (_proc != null && !_proc.HasExited)
        {
            try
            {
                _proc.StandardInput.Write('q');
                _proc.StandardInput.Flush();
            }
            catch { }

            if (!WaitForExit(StopTimeout))
            {
                try { _proc.Kill(true); } catch { }
                WaitForExit(KillDrainTimeout);
            }
        }
        else
        {
            WaitForExit(KillDrainTimeout);
        }
        DrainTask(_stdoutReader, StdoutDrainTimeout);
        WaitStderrClosed(StderrDrainTimeout);
        StopMicrophoneMonitor();
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        var tcs = _exitTcs;
        if (tcs == null) return true;
        try { return tcs.Task.Wait(timeout); }
        catch { return false; }
    }

    public string GetStderrLog()
    {
        lock (_lock) return _stderrLog.ToString();
    }

    /// <summary>
    /// Best-effort timestamp (UTC milliseconds since epoch) at which the
    /// microphone endpoint transitioned from active to inactive/unavailable.
    /// </summary>
    public long RuntimeAudioLostAtMs => Interlocked.Read(ref _runtimeAudioLostAtMs);

    private void StartMicrophoneMonitor(CaptureConfig cfg)
    {
        if (!cfg.Microphone || _microphoneStatusProvider == null)
            return;

        var deviceId = string.IsNullOrEmpty(cfg.MicDevice) ? "default" : cfg.MicDevice;
        var cts = new CancellationTokenSource();
        var oldCts = Interlocked.CompareExchange(ref _microphoneMonitorCts, cts, null);
        if (oldCts != null)
        {
            cts.Dispose();
            return;
        }

        _microphoneMonitorTask = Task.Run(() => RunMicrophoneMonitorAsync(deviceId, cts.Token), cts.Token);
    }

    private async Task RunMicrophoneMonitorAsync(string deviceId, CancellationToken cancellationToken)
    {
        bool? wasActive = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                MicrophoneStatus status;
                try
                {
                    status = await _microphoneStatusProvider!.GetStatusAsync(deviceId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    status = new MicrophoneStatus(null, null, null, null);
                }

                if (string.IsNullOrEmpty(status.State))
                {
                    await Task.Delay(MicrophoneMonitorInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                bool isActive = string.Equals(status.State, "Active", StringComparison.OrdinalIgnoreCase);

                if (wasActive == true && !isActive)
                {
                    Interlocked.CompareExchange(ref _runtimeAudioLostAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
                    break;
                }

                wasActive = isActive;
                await Task.Delay(MicrophoneMonitorInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void StopMicrophoneMonitor()
    {
        var cts = Interlocked.Exchange(ref _microphoneMonitorCts, null);
        if (cts == null)
            return;

        try { cts.Cancel(); } catch { }

        var task = _microphoneMonitorTask;
        if (task != null)
        {
            try { task.Wait(MicrophoneMonitorShutdownTimeout); } catch { }
        }

        try { cts.Dispose(); } catch { }
    }

    internal static List<string> BuildArgs(CaptureConfig cfg, string outputPath)
    {
        var args = new List<string>
        {
            "-y",
            "-nostats",
            "-progress", "pipe:1",
            "-f", "dshow",
            "-thread_queue_size", "512",
            "-i", $"audio={cfg.MicDevice}",
            "-af", TimestampCompensationFilter,
            "-acodec", "pcm_s16le",
            "-ar", "44100",
            "-ac", "2",
            outputPath
        };
        return args;
    }

    private static Task RunStdoutReader(TextReader reader, FFmpegProgressParser parser)
    {
        return Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                    parser.FeedLine(line);
            }
            catch { }
            finally
            {
                try { parser.Flush(); }
                catch { }
            }
        });
    }

    private static void DrainTask(Task? task, TimeSpan timeout)
    {
        if (task is null) return;
        try
        {
            if (task.IsCompleted) return;
            task.Wait(timeout);
        }
        catch { }
    }

    private void WaitStderrClosed(TimeSpan timeout)
    {
        try
        {
            _stderrClosed?.Wait(timeout);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _proc?.Dispose(); } catch { }
        try { _stderrClosed?.Dispose(); } catch { }
        StopMicrophoneMonitor();
    }
}
