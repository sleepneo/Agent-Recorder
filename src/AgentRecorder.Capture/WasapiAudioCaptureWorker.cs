using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Capture;

/// <summary>
/// WASAPI audio capture worker backed by the isolated AgentRecorder.AudioHelper
/// process. Converts the dshow device id to a CoreAudio endpoint id, launches
/// the helper, parses its IPC event stream, and exposes the standard
/// <see cref="IAudioCaptureWorker"/> lifecycle.
/// </summary>
public sealed class WasapiAudioCaptureWorker : IAudioCaptureWorker, IAudioHelperSummaryProvider
{
    private const int StdoutMaxChars = 65536;
    private const int StderrMaxChars = 32768;
    private const int ProtocolMaxBytes = 1048576;
    private const int ProtocolMaxEvents = 10000;
    private const int ProtocolMaxBlockLines = 64;
    private const int ProtocolMaxLineLength = 4096;

    private static readonly TimeSpan StdoutDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StderrDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan KillDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MicrophoneMonitorInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MicrophoneMonitorShutdownTimeout = TimeSpan.FromSeconds(3);

    private Process? _proc;
    private readonly BoundedStringBuilder _stderrLog = new(StderrMaxChars);
    private readonly BoundedStringBuilder _stdoutLog = new(StdoutMaxChars);
    private readonly object _lock = new();

    private Task? _stdoutReader;
    private Task? _watcher;
    private TaskCompletionSource<bool>? _exitTcs;
    private ManualResetEventSlim? _stderrClosed;

    private long _readyRaised;
    private long _mediaStartAnchorTicks;
    private long _runtimeAudioLostAtMs;
    private int _hasExited;
    private int _manualStopped;
    private int _terminalEventRaised;
    private int _startCalled;
    private int _protocolErrorRaised;
    private int _stdoutBytesRead;
    private int _eventCount;
    private long _finalValidated;
    private AudioHelperSessionSummary? _terminalSummary;
    private AudioHelperEvent? _startedEvent;
    private string? _expectedRecordingId;

    private readonly List<AudioHelperEvent> _events = new();

    private long _lastProgressElapsedMs = -1;
    private long _lastProgressWallElapsedMs = -1;
    private long _lastProgressBytesWritten = -1;
    private long _lastProgressEstimatedGapMs = -1;

    private IMicrophoneStatusProvider? _microphoneStatusProvider;
    private CancellationTokenSource? _microphoneMonitorCts;
    private Task? _microphoneMonitorTask;

    private string? _stopSignalPath;
    private string? _outputPath;
    private string? _allowedRoot;

    internal string? HelperExePathOverride { get; set; }
    internal string? EndpointIdOverride { get; set; }
    internal string? AllowedRootOverride { get; set; }
    internal string? StopSignalPathOverride { get; set; }
    internal string? HelperArgumentsOverride { get; set; }
    internal bool SkipMicrophoneStatusMonitor { get; set; }

    public event Action? AudioReady;
    public event Action<int, string>? NaturalExit;

    public DateTime? ReadyAtUtc { get; private set; }

    public long MediaStartAnchorTicks => Interlocked.Read(ref _mediaStartAnchorTicks);

    public string? OutputPath { get; private set; }

    public int ExitCode { get; private set; } = -1;

    public bool HasExited => Interlocked.CompareExchange(ref _hasExited, 0, 0) != 0;

    public bool IsAudioReady => ReadyAtUtc.HasValue;

    public long RuntimeAudioLostAtMs => Interlocked.Read(ref _runtimeAudioLostAtMs);

    public AudioHelperSessionSummary? GetTerminalSummary()
    {
        lock (_lock) return _terminalSummary;
    }

    internal bool IsProtocolError => Interlocked.CompareExchange(ref _protocolErrorRaised, 0, 0) != 0;

    public void SetMicrophoneStatusProvider(IMicrophoneStatusProvider? provider)
    {
        _microphoneStatusProvider = provider;
    }

    public void Start(CaptureConfig cfg, string outputPath)
    {
        if (Interlocked.Exchange(ref _startCalled, 1) != 0)
            throw new InvalidOperationException("WasapiAudioCaptureWorker.Start can only be called once.");

        if (string.IsNullOrWhiteSpace(cfg.MicDevice))
            throw new ArgumentException("Microphone device is required for WASAPI audio worker", nameof(cfg));

        OutputPath = outputPath;
        _outputPath = outputPath;
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var endpointId = EndpointIdOverride ?? CoreAudioCaptureStatusProvider.ToCoreAudioEndpointId(cfg.MicDevice);
        if (string.IsNullOrWhiteSpace(endpointId))
            throw new ApiException(400, "audio_endpoint_id_unmappable", "Could not map microphone device id to CoreAudio endpoint id.");

        _allowedRoot = AllowedRootOverride ?? dir ?? Path.GetTempPath();
        var recordingId = Path.GetFileNameWithoutExtension(outputPath) ?? "rec_unknown";
        _expectedRecordingId = recordingId;
        _stopSignalPath = StopSignalPathOverride ?? Path.Combine(_allowedRoot, $"{recordingId}_audio_stop.signal");

        // Clean up any stale stop signal from a previous run.
        TryDelete(_stopSignalPath);

        var helperExe = HelperExePathOverride ?? AudioHelperExePathResolver.Resolve();
        var args = BuildArgs(endpointId, outputPath, _allowedRoot, _stopSignalPath, recordingId, HelperArgumentsOverride);

        _proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helperExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false,
                RedirectStandardInput = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
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
                try { _stderrClosed?.Set(); } catch { }
                return;
            }
            _stderrLog.AppendLine(e.Data);
        };

        _proc.Exited += (_, _) =>
        {
            try
            {
                int code = _proc.ExitCode;
                lock (_lock) ExitCode = code;
            }
            catch { }
        };

        try
        {
            _proc.Start();
        }
        catch (Exception ex)
        {
            try { _stderrClosed?.Set(); } catch { }
            _exitTcs?.TrySetResult(true);
            throw new ApiException(500, "audio_helper_unavailable", "Failed to launch WASAPI audio helper: " + ex.Message);
        }

        _proc.BeginErrorReadLine();
        _stdoutReader = RunStdoutReader(_proc.StandardOutput);

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
                try { exited = _proc.WaitForExit(KillDrainTimeout); } catch { }
                try { if (exited) exitCode = _proc.ExitCode; } catch { }
            }

            DrainTask(_stdoutReader, StdoutDrainTimeout);
            WaitStderrClosed(StderrDrainTimeout);
            FinalValidateStream();
            StopMicrophoneMonitor();
            CleanupStopSignal();

            string stderr;
            string stdoutSummary;
            lock (_lock)
            {
                Interlocked.Exchange(ref _hasExited, 1);
                ExitCode = exitCode;
                stderr = _stderrLog.ToString();
                stdoutSummary = BuildStdoutSummary();
            }

            _exitTcs?.TrySetResult(true);

            if (Interlocked.CompareExchange(ref _manualStopped, 0, 0) == 0)
            {
                try { NaturalExit?.Invoke(exitCode, CombineStderr(stderr, stdoutSummary)); }
                catch { }
            }
        });
    }

    public void Stop()
    {
        Interlocked.Exchange(ref _manualStopped, 1);
        RequestHelperStopGraceful();
        WaitForHelperExit(StopTimeout);
        DrainTask(_stdoutReader, StdoutDrainTimeout);
        WaitStderrClosed(StderrDrainTimeout);
        FinalValidateStream();
        StopMicrophoneMonitor();
        CleanupStopSignal();
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
        return _stderrLog.ToString();
    }

    public void Dispose()
    {
        try
        {
            // Ensure a clean shutdown even if the caller never called Stop().
            Stop();
        }
        catch { }

        try { _proc?.Dispose(); } catch { }
        try { _stderrClosed?.Dispose(); } catch { }
        StopMicrophoneMonitor();
        CleanupStopSignal();
    }

    private void RequestHelperStopGraceful()
    {
        // Signal the helper to stop gracefully by creating the control file.
        if (!string.IsNullOrEmpty(_stopSignalPath))
        {
            try
            {
                File.WriteAllText(_stopSignalPath, "stop");
            }
            catch { }
        }
    }

    private void RequestHelperStopImmediate()
    {
        // Used when the protocol itself is broken: kill the helper process tree
        // without waiting for graceful cleanup.
        if (!string.IsNullOrEmpty(_stopSignalPath))
        {
            try { File.WriteAllText(_stopSignalPath, "stop"); } catch { }
        }

        try
        {
            if (_proc != null && !_proc.HasExited)
                _proc?.Kill(true);
        }
        catch { }
    }

    private void WaitForHelperExit(TimeSpan timeout)
    {
        var tcs = _exitTcs;
        if (tcs != null)
        {
            try { tcs.Task.Wait(timeout); } catch { }
        }

        try
        {
            if (_proc != null && !_proc.HasExited)
                _proc?.Kill(true);
        }
        catch { }

        if (tcs != null)
        {
            try { tcs.Task.Wait(KillDrainTimeout); } catch { }
        }
    }

    private static List<string> BuildArgs(string endpointId, string outputPath, string allowedRoot, string stopSignalPath, string recordingId, string? extraArgs)
    {
        var args = new List<string>
        {
            "--endpoint-id", endpointId,
            "--output", outputPath,
            "--allowed-root", allowedRoot,
            "--stop-signal", stopSignalPath,
            "--recording-id", recordingId
        };

        if (!string.IsNullOrWhiteSpace(extraArgs))
        {
            foreach (var token in extraArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                args.Add(token);
        }

        return args;
    }

    private Task RunStdoutReader(TextReader reader)
    {
        return Task.Run(() =>
        {
            try
            {
                string? line;
                var blockLines = new List<string>();
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length > ProtocolMaxLineLength)
                    {
                        RaiseProtocolError("protocol_line_too_long", $"Stdout line exceeded {ProtocolMaxLineLength} characters.");
                        continue;
                    }

                    _stdoutLog.AppendLine(line);

                    int bytesRead = Interlocked.Add(ref _stdoutBytesRead, Encoding.UTF8.GetByteCount(line) + 2); // +2 for \r\n worst case
                    if (bytesRead > ProtocolMaxBytes)
                    {
                        RaiseProtocolError("protocol_stdout_too_large", $"Stdout exceeded {ProtocolMaxBytes} bytes.");
                        break;
                    }

                    if (line.Trim().Length == 0)
                    {
                        if (blockLines.Count > 0)
                        {
                            ProcessEventBlock(blockLines);
                            blockLines.Clear();
                        }
                        continue;
                    }

                    blockLines.Add(line);
                    if (blockLines.Count > ProtocolMaxBlockLines)
                    {
                        RaiseProtocolError("protocol_block_too_large", $"Event block exceeded {ProtocolMaxBlockLines} lines.");
                        blockLines.Clear();
                    }
                }

                if (blockLines.Count > 0)
                    ProcessEventBlock(blockLines);
            }
            catch { }
        });
    }

    private void ProcessEventBlock(List<string> lines)
    {
        if (Interlocked.CompareExchange(ref _protocolErrorRaised, 0, 0) != 0)
            return;

        var evt = AudioHelperEventStreamParser.ParseEventBlock(lines);
        if (evt == null)
        {
            RaiseProtocolError("Missing RESULT block", "Event block did not contain a RESULT field.");
            return;
        }

        int eventCount = Interlocked.Increment(ref _eventCount);
        if (eventCount > ProtocolMaxEvents)
        {
            RaiseProtocolError($"Event stream exceeded {ProtocolMaxEvents} events.", $"Event stream exceeded {ProtocolMaxEvents} events.");
            return;
        }

        lock (_lock) _events.Add(evt);

        if (evt.Result == AudioHelperEventResult.Unknown)
        {
            RaiseProtocolError($"Unknown RESULT value in event block", "Event block contained an unknown RESULT value.");
            return;
        }

        if (evt.HasNumericParseError)
        {
            RaiseProtocolError("Event contained malformed numeric fields", "Event block contained a malformed numeric field.");
            return;
        }

        if (evt.Result == AudioHelperEventResult.Started)
        {
            ProcessStartedEvent(evt);
        }
        else if (evt.Result is AudioHelperEventResult.Ok or AudioHelperEventResult.Stopped or AudioHelperEventResult.Fail)
        {
            ProcessTerminalEvent(evt);
        }
        else if (evt.Result == AudioHelperEventResult.Progress)
        {
            ProcessProgressEvent(evt);
        }
    }

    private void ProcessProgressEvent(AudioHelperEvent evt)
    {
        if (Interlocked.CompareExchange(ref _readyRaised, 0, 0) == 0)
        {
            RaiseProtocolError("PROGRESS event received before STARTED.", "PROGRESS event received before STARTED.");
            return;
        }

        if (Interlocked.CompareExchange(ref _terminalEventRaised, 0, 0) != 0)
        {
            RaiseProtocolError("PROGRESS event received after terminal event.", "PROGRESS event received after terminal event.");
            return;
        }

        string? validationError = null;

        if (!evt.ElapsedMs.HasValue || evt.ElapsedMs.Value < 0)
            validationError = "PROGRESS event missing or negative ElapsedMs";
        else if (evt.ElapsedMs.Value < Interlocked.Read(ref _lastProgressElapsedMs))
            validationError = "PROGRESS event ElapsedMs regressed";

        if (validationError == null)
        {
            if (!evt.WallElapsedMs.HasValue || evt.WallElapsedMs.Value < 0)
                validationError = "PROGRESS event missing or negative WallElapsedMs";
            else if (evt.WallElapsedMs.Value < Interlocked.Read(ref _lastProgressWallElapsedMs))
                validationError = "PROGRESS event WallElapsedMs regressed";
        }

        if (validationError == null)
        {
            if (!evt.BytesWritten.HasValue || evt.BytesWritten.Value < 0)
                validationError = "PROGRESS event missing or negative BytesWritten";
            else if (evt.BytesWritten.Value < Interlocked.Read(ref _lastProgressBytesWritten))
                validationError = "PROGRESS event BytesWritten regressed";
        }

        if (validationError == null)
        {
            if (!evt.EstimatedGapMs.HasValue || evt.EstimatedGapMs.Value < 0)
                validationError = "PROGRESS event missing or negative EstimatedGapMs";
            else if (evt.EstimatedGapMs.Value < Interlocked.Read(ref _lastProgressEstimatedGapMs))
                validationError = "PROGRESS event EstimatedGapMs regressed";
        }

        if (validationError != null)
        {
            RaiseProtocolError(validationError, validationError);
            return;
        }

        Interlocked.Exchange(ref _lastProgressElapsedMs, evt.ElapsedMs!.Value);
        Interlocked.Exchange(ref _lastProgressWallElapsedMs, evt.WallElapsedMs!.Value);
        Interlocked.Exchange(ref _lastProgressBytesWritten, evt.BytesWritten!.Value);
        Interlocked.Exchange(ref _lastProgressEstimatedGapMs, evt.EstimatedGapMs!.Value);
    }

    private void ProcessStartedEvent(AudioHelperEvent evt)
    {
        if (Interlocked.Exchange(ref _readyRaised, 1) != 0)
        {
            RaiseProtocolError("protocol_duplicate_started", "Duplicate STARTED event received.");
            return;
        }

        if (Interlocked.CompareExchange(ref _terminalEventRaised, 0, 0) != 0)
        {
            RaiseProtocolError("protocol_event_after_terminal", "STARTED event received after terminal event.");
            return;
        }

        var validationErrors = new List<string>();

        if (string.IsNullOrEmpty(evt.RecordingId))
            validationErrors.Add("STARTED event missing required field: RecordingId");
        else if (!string.IsNullOrEmpty(_expectedRecordingId) && evt.RecordingId != _expectedRecordingId)
            validationErrors.Add($"RecordingId mismatch: expected '{_expectedRecordingId}', got '{evt.RecordingId}'");

        if (!evt.SampleRate.HasValue || evt.SampleRate.Value <= 0)
            validationErrors.Add("STARTED event missing or invalid field: SampleRate");

        if (!evt.Channels.HasValue || evt.Channels.Value <= 0)
            validationErrors.Add("STARTED event missing or invalid field: Channels");

        if (!evt.BitsPerSample.HasValue || evt.BitsPerSample.Value <= 0)
            validationErrors.Add("STARTED event missing or invalid field: BitsPerSample");

        if (!evt.FirstSampleAnchorTicks.HasValue || evt.FirstSampleAnchorTicks.Value <= 0)
            validationErrors.Add("STARTED event missing or invalid field: FirstSampleAnchorTicks");

        if (!evt.TimestampFrequency.HasValue || evt.TimestampFrequency.Value <= 0)
            validationErrors.Add("STARTED event missing or invalid field: TimestampFrequency");
        else if (evt.TimestampFrequency.Value != Stopwatch.Frequency)
            validationErrors.Add($"TimestampFrequency mismatch: helper={evt.TimestampFrequency.Value}, host={Stopwatch.Frequency}");

        bool nativeMediaCapture = string.Equals(evt.CaptureEngine, "windows-mediacapture", StringComparison.OrdinalIgnoreCase);
        if (!evt.BytesWritten.HasValue || evt.BytesWritten.Value < 0 || (!nativeMediaCapture && evt.BytesWritten.Value <= 0))
            validationErrors.Add("STARTED event missing or invalid field: BytesWritten");

        if (string.IsNullOrEmpty(evt.CaptureMethod))
            validationErrors.Add("STARTED event missing required field: CaptureMethod");

        if (evt.HasNumericParseError)
            validationErrors.Add("STARTED event contained malformed numeric fields");

        if (validationErrors.Count > 0)
        {
            lock (_lock) _startedEvent = evt;
            RaiseProtocolError("protocol_invalid_started", string.Join("; ", validationErrors));
            return;
        }

        lock (_lock) _startedEvent = evt;

        if (evt.FirstSampleAnchorTicks.HasValue && evt.FirstSampleAnchorTicks.Value > 0)
            Interlocked.Exchange(ref _mediaStartAnchorTicks, evt.FirstSampleAnchorTicks.Value);

        ReadyAtUtc = DateTime.UtcNow;
        try { AudioReady?.Invoke(); }
        catch { }
    }

    private void ProcessTerminalEvent(AudioHelperEvent evt)
    {
        if (Interlocked.Exchange(ref _terminalEventRaised, 1) != 0)
        {
            RaiseProtocolError("protocol_duplicate_terminal", $"Duplicate terminal event: {evt.Result}");
            return;
        }

        var events = new List<AudioHelperEvent>();
        lock (_lock)
        {
            if (_startedEvent != null)
                events.Add(_startedEvent);
        }
        events.Add(evt);
        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);
        lock (_lock) _terminalSummary = summary;
    }

    private void RaiseProtocolError(string reason, string detailedReason)
    {
        if (Interlocked.Exchange(ref _protocolErrorRaised, 1) != 0)
            return;

        // Stop the helper immediately on protocol error.
        RequestHelperStopImmediate();

        var summary = new AudioHelperSessionSummary
        {
            State = AudioHelperSessionState.MalformedSequence,
            ErrorCode = "audio_helper_protocol_error",
            Reason = reason
        };
        summary.ValidationErrors.Add(detailedReason);
        lock (_lock) _terminalSummary = summary;
    }



    private void FinalValidateStream()
    {
        // Ensure final validation runs exactly once and only after the process has
        // exited so the exit code cross-check is reliable.
        if (Interlocked.Exchange(ref _finalValidated, 1) != 0)
            return;

        List<AudioHelperEvent> events;
        lock (_lock)
        {
            events = new List<AudioHelperEvent>(_events);
        }

        var summary = AudioHelperEventStreamParser.ValidateAndSummarize(events);

        int exitCode;
        lock (_lock) exitCode = ExitCode;

        bool protocolIndicatesSuccess = summary.State == AudioHelperSessionState.Success ||
                                        summary.State == AudioHelperSessionState.Stopped;
        bool protocolIndicatesFailure = summary.State == AudioHelperSessionState.Failed ||
                                        summary.State == AudioHelperSessionState.MalformedSequence;

        // Protocol error was already raised in real-time: keep the protocol error
        // code but still record the final validation result.
        if (Interlocked.CompareExchange(ref _protocolErrorRaised, 0, 0) != 0)
        {
            summary.State = AudioHelperSessionState.MalformedSequence;
            summary.ErrorCode = "audio_helper_protocol_error";
        }
        // No terminal event from the stream: the helper exited without completing
        // the protocol. Cross-check the exit code.
        else if (summary.State == AudioHelperSessionState.MalformedSequence &&
            summary.ValidationErrors.Contains("No terminal event (OK/STOPPED/FAIL) found"))
        {
            summary.ErrorCode = "audio_helper_no_terminal_event";
            summary.Reason = "No terminal event received from helper";
            if (exitCode == 0)
                summary.ValidationErrors.Add("Exit code was 0 but no terminal event was received");
        }
        // Cross-check exit code against protocol terminal state.
        else if (exitCode == 0 && protocolIndicatesFailure)
        {
            var crossCheckSummary = new AudioHelperSessionSummary
            {
                State = AudioHelperSessionState.MalformedSequence,
                ErrorCode = "audio_helper_exit_protocol_mismatch",
                Reason = $"Exit code 0 but protocol state was {summary.State}"
            };
            crossCheckSummary.ValidationErrors.Add($"Exit code 0 but protocol state was {summary.State}");
            CopySummaryMetadata(summary, crossCheckSummary);
            summary = crossCheckSummary;
        }
        else if (exitCode != 0 && protocolIndicatesSuccess)
        {
            var crossCheckSummary = new AudioHelperSessionSummary
            {
                State = AudioHelperSessionState.MalformedSequence,
                ErrorCode = "audio_helper_exit_protocol_mismatch",
                Reason = $"Non-zero exit code ({exitCode}) but protocol state was {summary.State}"
            };
            crossCheckSummary.ValidationErrors.Add($"Non-zero exit code ({exitCode}) but protocol state was {summary.State}");
            CopySummaryMetadata(summary, crossCheckSummary);
            summary = crossCheckSummary;
        }

        lock (_lock) _terminalSummary = summary;
    }

    private static void CopySummaryMetadata(AudioHelperSessionSummary source, AudioHelperSessionSummary target)
    {
        target.RecordingId = source.RecordingId;
        target.SampleRate = source.SampleRate;
        target.Channels = source.Channels;
        target.BitsPerSample = source.BitsPerSample;
        target.FirstSampleAnchorTicks = source.FirstSampleAnchorTicks;
        target.TimestampFrequency = source.TimestampFrequency;
        target.CaptureMethod = source.CaptureMethod;
        target.CaptureEngine = source.CaptureEngine;
        target.FailureStage = source.FailureStage;
        target.EndpointId = source.EndpointId;
        target.PartialOutputPath = source.PartialOutputPath;
        target.SecondaryFailure = source.SecondaryFailure;

        // Preserve stream-health diagnostics across the exit-code cross-check so
        // a protocol mismatch never hides the real capture evidence.
        target.DurationMs = source.DurationMs;
        target.BytesWritten = source.BytesWritten;
        target.EstimatedGapMs = source.EstimatedGapMs;
        target.LastCallbackAgeMs = source.LastCallbackAgeMs;
        target.DiscontinuityCount = source.DiscontinuityCount;
        target.RecoveryCount = source.RecoveryCount;
        target.RecoveryAttempts = source.RecoveryAttempts;
        target.GapFilledBytes = source.GapFilledBytes;
        target.GapFilledMs = source.GapFilledMs;
        target.MaxEstimatedGapMs = source.MaxEstimatedGapMs;
        target.ContinuityStatus = source.ContinuityStatus;
    }

    private string BuildStdoutSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[audio-helper-stdout-summary]");
        var summary = _terminalSummary;
        if (summary != null)
        {
            sb.AppendLine($"State: {summary.State}");
            if (!string.IsNullOrEmpty(summary.ErrorCode))
                sb.AppendLine($"ErrorCode: {summary.ErrorCode}");
            if (!string.IsNullOrEmpty(summary.Reason))
                sb.AppendLine($"Reason: {summary.Reason}");
            if (summary.DurationMs.HasValue)
                sb.AppendLine($"DurationMs: {summary.DurationMs.Value}");
            if (summary.BytesWritten.HasValue)
                sb.AppendLine($"BytesWritten: {summary.BytesWritten.Value}");
            if (summary.EstimatedGapMs.HasValue)
                sb.AppendLine($"EstimatedGapMs: {summary.EstimatedGapMs.Value}");
            foreach (var err in summary.ValidationErrors)
                sb.AppendLine($"ValidationError: {err}");
        }
        else
        {
            sb.AppendLine("No terminal event received");
        }

        if (_stdoutLog.IsTruncated)
            sb.AppendLine("StdoutTruncated: true");
        if (_stderrLog.IsTruncated)
            sb.AppendLine("StderrTruncated: true");

        return sb.ToString();
    }

    private static string CombineStderr(string stderr, string stdoutSummary)
    {
        if (string.IsNullOrEmpty(stderr)) return stdoutSummary;
        if (string.IsNullOrEmpty(stdoutSummary)) return stderr;
        return stderr + "\n" + stdoutSummary;
    }

    private void CleanupStopSignal()
    {
        if (!string.IsNullOrEmpty(_stopSignalPath))
            TryDelete(_stopSignalPath);
    }

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch { }
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
        try { _stderrClosed?.Wait(timeout); }
        catch { }
    }

    private void StartMicrophoneMonitor(CaptureConfig cfg)
    {
        if (SkipMicrophoneStatusMonitor || !cfg.Microphone || _microphoneStatusProvider == null)
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
        try { cts.Dispose(); } catch { }

        var task = _microphoneMonitorTask;
        if (task != null)
        {
            try { task.Wait(MicrophoneMonitorShutdownTimeout); } catch { }
        }
    }
}
