using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using AgentRecorder.Capture;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

/// <summary>
/// Tests for <see cref="WgcContinuousManagedSession"/> using a fake process
/// transport. No real WGC capture or GUI is exercised, except the dedicated
/// real-process-tree fixture test. The class joins the shared
/// NonParallel-RealProcess collection so its real PowerShell/ping fixture
/// never runs concurrently with other real process-tree fixtures.
/// </summary>
[Collection("NonParallel-RealProcess")]
public sealed class WgcContinuousManagedSessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITestOutputHelper _output;
    private readonly List<IDisposable> _disposables = new();

    public WgcContinuousManagedSessionTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTests", $"wgc-managed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch { /* best effort */ }
        }

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best effort */ }
    }

    private WgcContinuousSessionOptions CreateOptions(string recordingId, Action<WgcContinuousSessionOptions>? configure = null)
    {
        var opts = new WgcContinuousSessionOptions
        {
            HelperExePath = Path.Combine(_tempDir, "wgc-native-helper.exe"),
            RecordingId = recordingId,
            DisplayX = -100,
            DisplayY = 0,
            DisplayWidth = 1920,
            DisplayHeight = 1080,
            OutputPath = Path.Combine(_tempDir, $"{recordingId}.mp4"),
            DurationMs = 5000,
            Fps = 30,
            BeginSignalPath = Path.Combine(_tempDir, $"{recordingId}.begin.signal"),
            BeginToken = $"token-{recordingId}-{Guid.NewGuid():N}",
            BeginTimeoutMs = 30000,
            StopSignalPath = Path.Combine(_tempDir, $"{recordingId}.stop.signal"),
            ProcessTimeoutMs = 30000,
            StopWaitTimeoutMs = 5000
        };

        File.WriteAllText(opts.HelperExePath, "fake");
        configure?.Invoke(opts);
        return opts;
    }

    private static void CreatePlaceholderMp4(string path, long size)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(size);
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!predicate() && sw.Elapsed < timeout)
            await Task.Delay(10);

        if (!predicate())
            throw new TimeoutException("Condition was not met within the allotted timeout.");
    }

    private static string[] Started(string recordingId, string outputPath) => new[]
    {
        "RESULT: STARTED",
        $"RecordingId: {recordingId}",
        $"Output: {outputPath}",
        "Container: mp4",
        "Codec: h264",
        "Fps: 30",
        "Width: 1920",
        "Height: 1080",
        "CaptureMethod: WGC_D3D11_FRAME_STREAM",
        "" // blank-line event separator
    };

    private static string[] Progress(long frames, long elapsedMs, long bytesWritten = 0) => new[]
    {
        "RESULT: PROGRESS",
        $"FramesCaptured: {frames}",
        "FramesDropped: 0",
        $"ElapsedMs: {elapsedMs}",
        $"BytesWritten: {bytesWritten}",
        "" // blank-line event separator
    };

    private static string[] Ok(long frames = 300, long durationMs = 5000, long fileSize = 15000000) => new[]
    {
        "RESULT: OK",
        $"FramesCaptured: {frames}",
        "FramesDropped: 0",
        $"DurationMs: {durationMs}",
        $"FileSize: {fileSize} bytes",
        "Width: 1920",
        "Height: 1080",
        "" // blank-line event separator
    };

    private static string[] Stopped(long frames = 150, long durationMs = 2500, long fileSize = 7500000) => new[]
    {
        "RESULT: STOPPED",
        "StopReason: user_requested",
        $"FramesCaptured: {frames}",
        "FramesDropped: 0",
        $"DurationMs: {durationMs}",
        $"FileSize: {fileSize} bytes",
        "Width: 1920",
        "Height: 1080",
        "" // blank-line event separator
    };

    private static string[] RegionStarted(string recordingId, string outputPath) => new[]
    {
        "RESULT: STARTED",
        $"RecordingId: {recordingId}",
        $"Output: {outputPath}",
        "Container: mp4",
        "Codec: h264",
        "Fps: 30",
        "Width: 640",
        "Height: 480",
        "CaptureMethod: WGC_D3D11_REGION_FRAME_STREAM",
        ""
    };

    private static string[] RegionOk(long fileSize = 15000000) => new[]
    {
        "RESULT: OK",
        "FramesCaptured: 150",
        "FramesDropped: 0",
        "DurationMs: 5000",
        $"FileSize: {fileSize} bytes",
        "Width: 640",
        "Height: 480",
        ""
    };

    private static string[] Fail(string reason, string errorCode, string? partialPath = null)
    {
        var lines = new List<string>
        {
            "RESULT: FAIL",
            $"ErrorCode: {errorCode}",
            $"Reason: {reason}",
            "FramesCaptured: 0",
            "BytesWritten: 0"
        };
        if (!string.IsNullOrEmpty(partialPath))
            lines.Add($"PartialOutputPath: {partialPath}");
        lines.Add(""); // blank-line event separator
        return lines.ToArray();
    }

    // -----------------------------------------------------------------
    // Fake process transport
    // -----------------------------------------------------------------

    private sealed class FakeWgcContinuousProcess : IWgcContinuousProcess
    {
        private readonly List<string> _initialStdout;
        private readonly List<string>? _finalStdout;
        private readonly List<string> _stderr;
        private readonly TimeSpan? _initialDelay;
        private readonly TimeSpan? _exitDelay;
        private readonly bool _ignoreStopSignal;
        private readonly bool _createOutputFile;
        private readonly long _outputFileSize;
        private readonly string? _outputFilePath;
        private readonly string? _autoContinueOnStopSignalPath;
        private string? _waitForBeginSignalPath;
        private readonly TaskCompletionSource? _startEntered;
        private readonly TaskCompletionSource? _startRelease;
        private readonly TaskCompletionSource _continueSignal = new();
        private readonly TaskCompletionSource _exitTcs = new();
        private readonly TaskCompletionSource _killSignal = new();
        private int _started;
        private int _startInvocationCount;

        public int Id { get; set; } = 4242;
        public int ExitCode { get; set; }
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public Stream StandardOutputStream { get; private set; } = Stream.Null;
        public Stream StandardErrorStream { get; private set; } = Stream.Null;
        public string? CapturedFileName { get; private set; }
        public IReadOnlyList<string>? CapturedArguments { get; private set; }
        public bool WasKilled => _killSignal.Task.IsCompleted;
        public int StartInvocationCount => _startInvocationCount;
        public Task StartEnteredTask => _startEntered?.Task ?? Task.CompletedTask;

        /// <summary>
        /// When set, the fake waits until this file exists before emitting any
        /// capture events. This mirrors the real helper, which blocks on the
        /// begin-signal file, and eliminates races where events arrive before
        /// the session has finished authorization.
        /// </summary>
        public string? WaitForBeginSignalPath
        {
            get => _waitForBeginSignalPath;
            set => _waitForBeginSignalPath = value;
        }

        public FakeWgcContinuousProcess(
            IEnumerable<string> initialStdout,
            IEnumerable<string>? finalStdout = null,
            IEnumerable<string>? stderr = null,
            int exitCode = 0,
            TimeSpan? initialDelay = null,
            TimeSpan? exitDelay = null,
            bool ignoreStopSignal = false,
            bool createOutputFile = false,
            long outputFileSize = 0,
            string? outputFilePath = null,
            string? autoContinueOnStopSignalPath = null,
            string? waitForBeginSignalPath = null,
            TaskCompletionSource? startEntered = null,
            TaskCompletionSource? startRelease = null)
        {
            _initialStdout = initialStdout.ToList();
            _finalStdout = finalStdout?.ToList();
            _stderr = stderr?.ToList() ?? new List<string>();
            ExitCode = exitCode;
            _initialDelay = initialDelay;
            _exitDelay = exitDelay;
            _ignoreStopSignal = ignoreStopSignal;
            _createOutputFile = createOutputFile;
            _outputFileSize = outputFileSize;
            _outputFilePath = outputFilePath;
            _autoContinueOnStopSignalPath = autoContinueOnStopSignalPath;
            _waitForBeginSignalPath = waitForBeginSignalPath;
            _startEntered = startEntered;
            _startRelease = startRelease;
        }

        public void Start(string fileName, IReadOnlyList<string> argumentList)
        {
            Interlocked.Increment(ref _startInvocationCount);
            if (Interlocked.Exchange(ref _started, 1) != 0)
                throw new InvalidOperationException("Already started");

            CapturedFileName = fileName;
            CapturedArguments = argumentList.ToList();

            // Publish that Start has been entered and optionally block until the
            // test releases it. This lets deterministic race tests observe the
            // window before the process object is actually published.
            _startEntered?.TrySetResult();
            _startRelease?.Task.Wait();

            var stdoutChannel = Channel.CreateUnbounded<byte>();
            var stderrChannel = Channel.CreateUnbounded<byte>();

            StandardOutputStream = new ChannelStream(stdoutChannel.Reader);
            StandardErrorStream = new ChannelStream(stderrChannel.Reader);

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_initialDelay.HasValue)
                        await Task.Delay(_initialDelay.Value);

                    if (!string.IsNullOrEmpty(_waitForBeginSignalPath))
                    {
                        // Wait for the session to authorize before emitting any
                        // capture events, eliminating races between authorization
                        // failures and terminal-event processing.
                        while (!File.Exists(_waitForBeginSignalPath) && !_killSignal.Task.IsCompleted)
                        {
                            await Task.Delay(10);
                        }
                    }

                    foreach (var line in _initialStdout)
                        await WriteLineAsync(stdoutChannel.Writer, line);

                    if (_finalStdout != null && _finalStdout.Count > 0)
                    {
                        if (!string.IsNullOrEmpty(_autoContinueOnStopSignalPath))
                        {
                            // Poll for stop signal file with short interval.
                            while (!File.Exists(_autoContinueOnStopSignalPath) && !_killSignal.Task.IsCompleted)
                            {
                                await Task.Delay(10);
                            }

                            // Once the stop signal is observed, allow the
                            // final stdout to proceed without further caller
                            // coordination.
                            _continueSignal.TrySetResult();
                        }

                        await _continueSignal.Task;
                        foreach (var line in _finalStdout)
                            await WriteLineAsync(stdoutChannel.Writer, line);
                    }

                    stdoutChannel.Writer.Complete();

                    foreach (var line in _stderr)
                        await WriteLineAsync(stderrChannel.Writer, line);
                    stderrChannel.Writer.Complete();

                    if (_createOutputFile && !string.IsNullOrEmpty(_outputFilePath))
                    {
                        var dir = Path.GetDirectoryName(_outputFilePath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        using var fs = new FileStream(_outputFilePath, FileMode.Create, FileAccess.Write);
                        fs.SetLength(_outputFileSize);
                    }

                    if (_ignoreStopSignal)
                        await _killSignal.Task;

                    if (_exitDelay.HasValue)
                        await Task.Delay(_exitDelay.Value);

                    _exitTcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    stdoutChannel.Writer.TryComplete(ex);
                    stderrChannel.Writer.TryComplete(ex);
                    _exitTcs.TrySetException(ex);
                }
            });
        }

        public void Continue() => _continueSignal.TrySetResult();

        public void ReleaseStart() => _startRelease?.TrySetResult();

        public void KillEntireTree()
        {
            _killSignal.TrySetResult();
            _exitTcs.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exitTcs.Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
            _exitTcs.TrySetResult();
        }

        private static async Task WriteLineAsync(ChannelWriter<byte> writer, string line)
        {
            foreach (var b in Encoding.UTF8.GetBytes(line))
                await writer.WriteAsync(b);
            await writer.WriteAsync((byte)'\n');
        }
    }

    /// <summary>
    /// Exposes a <see cref="ChannelReader{byte}"/> as a readable Stream so the
    /// fake process can produce stdout/stderr byte-by-byte without materializing
    /// full lines or huge strings upfront.
    /// </summary>
    private sealed class ChannelStream : Stream
    {
        private readonly ChannelReader<byte> _reader;
        private bool _completed;

        public ChannelStream(ChannelReader<byte> reader) => _reader = reader;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_completed || count == 0) return 0;

            int totalRead = 0;
            while (totalRead < count)
            {
                if (_reader.TryRead(out var b))
                {
                    buffer[offset + totalRead] = b;
                    totalRead++;
                }
                else if (_reader.Completion.IsCompleted)
                {
                    _completed = true;
                    break;
                }
                else
                {
                    if (totalRead > 0) break;
                    if (!await _reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _completed = true;
                        break;
                    }
                }
            }
            return totalRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Test seam for authorization signal writes. Blocks the file I/O at a
    /// controllable point so tests can prove the session lock is not held
    /// during I/O and can deterministically exercise cancellation/dispose races.
    /// </summary>
    private sealed class ControllableSignalWriter : IAuthorizationSignalWriter
    {
        private readonly TaskCompletionSource _enteredTcs = new();
        private readonly TaskCompletionSource _ioPointTcs = new();
        private readonly TaskCompletionSource _releaseTcs = new();

        public Task EnteredTask => _enteredTcs.Task;
        public Task IoPointTask => _ioPointTcs.Task;

        public async Task WriteBeginTokenAsync(
            string tmpPath,
            string finalPath,
            string token,
            CancellationToken cancellationToken)
        {
            _enteredTcs.TrySetResult();
            _ioPointTcs.TrySetResult();

            // The test observes IoPointTask and then either releases us or
            // cancels the token. Reaching this await proves the session lock was
            // released before the file operation began.
            await _releaseTcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            var dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(tmpPath, token, cancellationToken).ConfigureAwait(false);
            File.Move(tmpPath, finalPath, overwrite: true);
        }

        public void Release() => _releaseTcs.TrySetResult();
    }

    /// <summary>
    /// Writes the tmp file and then waits for cancellation. Lets tests prove
    /// that cancellation after the tmp exists still leaves no token-bearing
    /// artifact behind.
    /// </summary>
    private sealed class TmpThenCanceledWriter : IAuthorizationSignalWriter
    {
        private readonly TaskCompletionSource _tmpWrittenTcs = new();

        public Task TmpWrittenTask => _tmpWrittenTcs.Task;

        public async Task WriteBeginTokenAsync(
            string tmpPath,
            string finalPath,
            string token,
            CancellationToken cancellationToken)
        {
            var dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(tmpPath, token, cancellationToken).ConfigureAwait(false);
            _tmpWrittenTcs.TrySetResult();

            // Wait until cancellation (or a safety timeout in case the token is
            // never canceled, so the test does not hang).
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tmpPath, finalPath, overwrite: true);
        }
    }

    /// <summary>
    /// A stream that yields a single repeated byte up to a bounded length. Used
    /// to simulate oversized stdout/stderr without allocating the full payload.
    /// </summary>
    private sealed class RepeatingByteStream : Stream
    {
        private readonly long _length;
        private readonly byte _value;
        private long _position;

        public RepeatingByteStream(long length, byte value = (byte)'x')
        {
            _length = length;
            _value = value;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length) return 0;
            long remaining = _length - _position;
            int toRead = (int)Math.Min(count, remaining);
            Array.Fill(buffer, _value, offset, toRead);
            _position += toRead;
            return toRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Yield();
            return Read(buffer, offset, count);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Process fake that produces an unbounded-length line on stdout to verify
    /// the parser enforces MaxSingleLineLength before materializing the line.
    /// </summary>
    private sealed class HugeStdoutProcess : IWgcContinuousProcess
    {
        private readonly long _byteLength;
        private readonly TaskCompletionSource _exitTcs = new();
        private readonly TaskCompletionSource _killSignal = new();
        private Stream? _stdoutStream;

        public HugeStdoutProcess(long byteLength) => _byteLength = byteLength;

        public int Id => 4242;
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public int ExitCode => _killSignal.Task.IsCompleted ? -1 : 0;
        public Stream StandardOutputStream => _stdoutStream ?? Stream.Null;
        public Stream StandardErrorStream => Stream.Null;

        public void Start(string fileName, IReadOnlyList<string> argumentList)
        {
            _stdoutStream = new RepeatingByteStream(_byteLength);
        }

        public void KillEntireTree()
        {
            _killSignal.TrySetResult();
            _exitTcs.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exitTcs.Task.WaitAsync(cancellationToken);

        public void Dispose() => _exitTcs.TrySetResult();
    }

    /// <summary>
    /// Process fake that produces a bounded stdout event sequence and an
    /// unbounded stderr stream to verify the stderr tail buffer stays bounded.
    /// </summary>
    private sealed class HugeStderrProcess : IWgcContinuousProcess
    {
        private readonly long _stderrByteLength;
        private readonly TaskCompletionSource _exitTcs = new();
        private readonly TaskCompletionSource _killSignal = new();
        private Stream? _stdoutStream;
        private Stream? _stderrStream;

        private readonly string _outputPath;

        public HugeStderrProcess(long stderrByteLength, string outputPath)
        {
            _stderrByteLength = stderrByteLength;
            _outputPath = outputPath;
        }

        public int Id => 4242;
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public int ExitCode => _killSignal.Task.IsCompleted ? -1 : 0;
        public Stream StandardOutputStream => _stdoutStream ?? Stream.Null;
        public Stream StandardErrorStream => _stderrStream ?? Stream.Null;
        public string? WaitForBeginSignalPath { get; set; }

        public void Start(string fileName, IReadOnlyList<string> argumentList)
        {
            var stdoutText = $"RESULT: STARTED\nRecordingId: r\nOutput: {_outputPath}\nContainer: mp4\nCodec: h264\nFps: 30\nWidth: 1920\nHeight: 1080\nCaptureMethod: WGC_D3D11_FRAME_STREAM\n\nRESULT: OK\nFramesCaptured: 1\nDurationMs: 1000\nFileSize: 100 bytes\nWidth: 1920\nHeight: 1080\n\n";
            var stdout = new MemoryStream(Encoding.UTF8.GetBytes(stdoutText));
            var stderr = new RepeatingByteStream(_stderrByteLength, (byte)'y');

            // Gate stream reads on the begin signal so StartAsync returns
            // immediately and the session can authorize before events are parsed.
            if (!string.IsNullOrEmpty(WaitForBeginSignalPath))
            {
                _stdoutStream = new AuthorizationGatedStream(stdout, WaitForBeginSignalPath, _killSignal.Task);
                _stderrStream = new AuthorizationGatedStream(stderr, WaitForBeginSignalPath, _killSignal.Task);
            }
            else
            {
                _stdoutStream = stdout;
                _stderrStream = stderr;
            }

            // The helper would naturally exit after emitting the terminal OK.
            // Completing the TCS here lets the watcher finalize without waiting
            // for the full 5 MB stderr stream to be consumed.
            _exitTcs.TrySetResult();
        }

        public void KillEntireTree()
        {
            _killSignal.TrySetResult();
            _exitTcs.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exitTcs.Task.WaitAsync(cancellationToken);

        public void Dispose() => _exitTcs.TrySetResult();
    }

    /// <summary>
    /// Wraps a stream and caps each ReadAsync to a maximum number of bytes,
    /// forcing chunk-boundary conditions for the parser.
    /// </summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxChunk;

        public ChunkedStream(Stream inner, int maxChunk)
        {
            _inner = inner;
            _maxChunk = maxChunk;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await _inner.ReadAsync(buffer.AsMemory(offset, Math.Min(count, _maxChunk)), cancellationToken).ConfigureAwait(false);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Wraps a stream and delays the first read until a signal file exists or
    /// the process is killed. Used to keep capture events from being parsed
    /// before the session has finished authorizing.
    /// </summary>
    private sealed class AuthorizationGatedStream : Stream
    {
        private readonly Stream _inner;
        private readonly string _signalPath;
        private readonly Task _killTask;
        private int _gateOpened;

        public AuthorizationGatedStream(Stream inner, string signalPath, Task killTask)
        {
            _inner = inner;
            _signalPath = signalPath;
            _killTask = killTask;
        }

        private async Task WaitForGateAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _gateOpened, 1) != 0)
                return;

            while (!File.Exists(_signalPath) && !_killTask.IsCompleted)
            {
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Process fake that returns a precisely chunked stdout byte stream. Used
    /// to verify CRLF spanning chunk boundaries and EOF without trailing newline.
    /// </summary>
    private sealed class PreciseChunkStdoutProcess : IWgcContinuousProcess
    {
        private readonly byte[] _bytes;
        private readonly int _chunkSize;
        private readonly TimeSpan? _autoExitDelay;
        private readonly TaskCompletionSource _exitTcs = new();
        private readonly TaskCompletionSource _killSignal = new();
        private Stream? _stdoutStream;

        public PreciseChunkStdoutProcess(byte[] bytes, int chunkSize, TimeSpan? autoExitDelay = null)
        {
            _bytes = bytes;
            _chunkSize = chunkSize;
            _autoExitDelay = autoExitDelay;
        }

        public int Id => 4242;
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public int ExitCode => _killSignal.Task.IsCompleted ? -1 : 0;
        public Stream StandardOutputStream => _stdoutStream ?? Stream.Null;
        public Stream StandardErrorStream => Stream.Null;
        public string? WaitForBeginSignalPath { get; set; }

        public void Start(string fileName, IReadOnlyList<string> argumentList)
        {
            var chunked = new ChunkedStream(new MemoryStream(_bytes), _chunkSize);
            if (!string.IsNullOrEmpty(WaitForBeginSignalPath))
            {
                // Wrap the chunked stream so reads block until authorization has
                // written the begin signal. This prevents capture events from
                // being parsed before the session is authorized.
                _stdoutStream = new AuthorizationGatedStream(chunked, WaitForBeginSignalPath, _killSignal.Task);
            }
            else
            {
                _stdoutStream = chunked;
            }

            if (_autoExitDelay.HasValue)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(_autoExitDelay.Value);
                    _exitTcs.TrySetResult();
                });
            }
        }

        public void KillEntireTree()
        {
            _killSignal.TrySetResult();
            _exitTcs.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exitTcs.Task.WaitAsync(cancellationToken);

        public void Dispose() => _exitTcs.TrySetResult();
    }

    // -----------------------------------------------------------------
    // Real process-tree fixture
    // -----------------------------------------------------------------

    private sealed class RealProcessTreeFixtureProcess : IWgcContinuousProcess
    {
        private readonly string _childPidFilePath;
        private readonly string _scriptPath;
        private Process? _process;
        private readonly TaskCompletionSource _exitTcs = new();
        private int _exitCode = -1;

        public int Id => _process?.Id ?? 0;
        public bool HasExited => _process?.HasExited ?? true;
        public int ExitCode => _exitCode;
        public Stream StandardOutputStream => _process?.StandardOutput.BaseStream ?? Stream.Null;
        public Stream StandardErrorStream => _process?.StandardError.BaseStream ?? Stream.Null;

        public RealProcessTreeFixtureProcess(string childPidFilePath)
        {
            _childPidFilePath = childPidFilePath;
            _scriptPath = childPidFilePath + ".ps1";
        }

        public int? ChildPid
        {
            get
            {
                try
                {
                    if (File.Exists(_childPidFilePath))
                    {
                        // BOM-tolerant read: the writer now emits plain ASCII
                        // digits with no BOM and no newline, but tolerate a BOM
                        // so a stale file from an older writer still parses.
                        var text = File.ReadAllText(_childPidFilePath).Trim().TrimStart('\uFEFF');
                        if (int.TryParse(text, out var pid))
                            return pid;
                    }
                }
                catch { }
                return null;
            }
        }

        public void Start(string fileName, IReadOnlyList<string> argumentList)
        {
            // Deterministic encoding: write bare ASCII digits via
            // [System.IO.File]::WriteAllText (no BOM, no newline). The previous
            // Out-File -Encoding utf8 form emits a BOM on Windows PowerShell
            // 5.1 but not on PowerShell 7+, making the PID boundary depend on
            // whichever powershell.exe is resolved and on runtime BOM detection.
            File.WriteAllText(_scriptPath,
                "$child = Start-Process -FilePath 'ping.exe' -ArgumentList '-n 30 127.0.0.1' -PassThru -NoNewWindow; " +
                "[System.IO.File]::WriteAllText('" + _childPidFilePath.Replace("'", "''") + "', \"$($child.Id)\"); " +
                "$child.WaitForExit()");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{_scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = Process.Start(psi)!;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) =>
            {
                try { _exitCode = _process.ExitCode; }
                catch { }
                _exitTcs.TrySetResult();
            };
        }

        public void KillEntireTree()
        {
            try { _process?.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Kills every process this fixture owns: the child ping recorded in
        /// the PID file first, then the powershell root. The child is only
        /// killed when the live process image name is still "ping", so a
        /// recycled PID belonging to an unrelated process is never touched.
        /// </summary>
        public void KillOwnedProcessTree()
        {
            var childPid = ChildPid;
            if (childPid.HasValue)
            {
                try
                {
                    using var child = Process.GetProcessById(childPid.Value);
                    child.Refresh();
                    if (!child.HasExited &&
                        string.Equals(child.ProcessName, "ping", StringComparison.OrdinalIgnoreCase))
                    {
                        child.Kill(entireProcessTree: true);
                        child.WaitForExit(5000);
                    }
                }
                catch { /* already gone or access denied — best effort */ }
            }

            try { _process?.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _exitTcs.Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
            // Never leave the fixture's real processes behind, even when the
            // owning test failed before its own cleanup assertions ran.
            KillOwnedProcessTree();
            try { _process?.Dispose(); } catch { }
            try { File.Delete(_scriptPath); } catch { }
            try { File.Delete(_childPidFilePath); } catch { }
        }
    }

    // -----------------------------------------------------------------
    // Lifecycle / Start
    // -----------------------------------------------------------------

    [Fact]
    public async Task RegionSession_UsesDisplayAndRegionArguments_AndWaitsForAuthorization()
    {
        var recId = $"region_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o =>
        {
            o.TargetKind = WgcContinuousTargetKind.Region;
            o.DisplayX = -1920;
            o.DisplayY = -200;
            o.DisplayWidth = 1920;
            o.DisplayHeight = 1080;
            o.RegionX = -1800;
            o.RegionY = -100;
            o.RegionWidth = 640;
            o.RegionHeight = 480;
        });
        const long fileSize = 15000000;
        var stdout = RegionStarted(recId, opts.OutputPath)
            .Concat(RegionOk(fileSize))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(
            stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath,
            waitForBeginSignalPath: opts.BeginSignalPath);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();

        Assert.Equal(WgcContinuousManagedSessionState.WaitingForAuthorization, session.State);
        Assert.False(File.Exists(opts.BeginSignalPath));
        Assert.Contains("--capture-continuous-region", fake.CapturedArguments!);
        Assert.Contains("--display-bounds", fake.CapturedArguments!);
        Assert.Contains("-1920,-200,1920,1080", fake.CapturedArguments!);
        Assert.Contains("--region-bounds", fake.CapturedArguments!);
        Assert.Contains("-1800,-100,640,480", fake.CapturedArguments!);

        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.True(result.OutputFileExists);
        Assert.Equal(fileSize, result.OutputFileSizeBytes);
    }

    [Fact]
    public async Task StartAsync_ReturnsBeforeSessionCompletes()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath)
            .Concat(Progress(1, 100))
            .Concat(Progress(300, 5000))
            .Concat(Ok(300, 5000, fileSize))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            initialDelay: TimeSpan.FromMilliseconds(50),
            exitDelay: TimeSpan.FromMilliseconds(100),
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        var sw = Stopwatch.StartNew();
        await session.StartAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), "StartAsync must return quickly");
        Assert.Equal(WgcContinuousManagedSessionState.WaitingForAuthorization, session.State);

        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.True(result.OutputFileExists, "Output file must exist");
        Assert.Equal(fileSize, result.OutputFileSizeBytes);
    }

    [Fact]
    public async Task StartAsync_WithoutAuthorization_Dispose_CleansControlFiles()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o => o.ProcessTimeoutMs = 500);
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>(), ignoreStopSignal: true);
        var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        Assert.False(File.Exists(opts.BeginSignalPath), "Begin signal must not be created before authorization");

        session.Dispose();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
        Assert.False(File.Exists(opts.BeginSignalPath), "Begin signal must be cleaned up");
        Assert.False(File.Exists(opts.StopSignalPath), "Stop signal must be cleaned up");
        Assert.True(fake.WasKilled || fake.HasExited, "Process must be terminated");
    }

    [Fact]
    public async Task StartAsync_AlreadyCancelled_DoesNotStartProcess()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>());
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await session.StartAsync(cts.Token);

        Assert.Equal(0, fake.StartInvocationCount);
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
        Assert.Equal("lifecycle", result.FailurePhase);
        Assert.Equal("caller_cancelled", result.FailureCategory);
    }

    [Fact]
    public async Task StartAsync_CancelledDuringStart_KillsProcessAndCompletesOnce()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>(), initialDelay: TimeSpan.FromMilliseconds(200));
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        using var cts = new CancellationTokenSource();

        var startTask = session.StartAsync(cts.Token);
        // Cancel shortly after StartAsync begins but while it is still in progress.
        await Task.Delay(50);
        cts.Cancel();
        await startTask;

        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
        Assert.Equal(1, fake.StartInvocationCount);
        Assert.True(fake.WasKilled || fake.HasExited, "Process must be terminated after cancellation");
    }

    [Fact]
    public async Task StartAsync_PreStartSignalFilesRemoved()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        File.WriteAllText(opts.BeginSignalPath, "stale-begin");
        File.WriteAllText(opts.BeginSignalPath + ".tmp", "stale-tmp");
        File.WriteAllText(opts.StopSignalPath, "stale-stop");

        var stdout = Started(recId, opts.OutputPath).Concat(Ok()).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout, initialDelay: TimeSpan.FromMilliseconds(50));
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();

        Assert.False(File.Exists(opts.BeginSignalPath), "Stale begin signal must be removed");
        Assert.False(File.Exists(opts.BeginSignalPath + ".tmp"), "Stale begin tmp must be removed");
        Assert.False(File.Exists(opts.StopSignalPath), "Stale stop signal must be removed");
        Assert.Equal(1, fake.StartInvocationCount);
    }

    [Fact]
    public async Task StartAsync_PreStartCleanupFailure_DoesNotStartProcess()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        File.WriteAllText(opts.BeginSignalPath, "locked");
        using var lockStream = new FileStream(opts.BeginSignalPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var fake = new FakeWgcContinuousProcess(Array.Empty<string>());
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();

        Assert.Equal(0, fake.StartInvocationCount);
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("lifecycle", result.FailurePhase);
        Assert.Equal("pre_start_cleanup_failed", result.FailureCategory);
    }

    [Fact]
    public async Task StartAsync_Concurrent_OnlyOneStartsProcess()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>(), initialDelay: TimeSpan.FromMilliseconds(100));
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        var exceptions = new List<Exception>();
        var start1 = Task.Run(() =>
        {
            try { session.StartAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
        });
        var start2 = Task.Run(() =>
        {
            try { session.StartAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { lock (exceptions) exceptions.Add(ex); }
        });

        await Task.WhenAll(start1, start2);

        Assert.Equal(1, fake.StartInvocationCount);
        Assert.Single(exceptions);
        Assert.Contains("already been started", exceptions[0].GetBaseException().Message);
    }

    [Fact]
    public async Task Start_DisposeRace_TerminatesLateProcess()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var startEntered = new TaskCompletionSource();
        var startRelease = new TaskCompletionSource();
        var fake = new FakeWgcContinuousProcess(
            Array.Empty<string>(),
            startEntered: startEntered,
            startRelease: startRelease);
        var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        // StartAsync calls _process.Start synchronously. Run it on another
        // thread so we can dispose while Start is blocked inside the fake.
        var startTask = Task.Run(() => session.StartAsync());
        await fake.StartEnteredTask.WaitAsync(TimeSpan.FromSeconds(5));

        // At this point Start has been entered but has not yet published the
        // process streams. Dispose must win the completion race.
        session.Dispose();

        // Now release the blocked Start. StartAsync must detect that completion
        // has already been entered and terminate the late process.
        fake.ReleaseStart();
        await startTask.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
        Assert.True(fake.WasKilled, "Late process must be terminated when Start loses to Dispose");
        Assert.Equal(1, fake.StartInvocationCount);
        Assert.False(File.Exists(opts.BeginSignalPath), "No begin signal should be written");
        Assert.False(File.Exists(opts.StopSignalPath), "No stop signal should be written");
    }

    // -----------------------------------------------------------------
    // Consent / Authorization
    // -----------------------------------------------------------------

    [Fact]
    public async Task AuthorizeCapture_WritesCorrectToken_TokenNotInDiagnostics()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath)
            .Concat(Ok(fileSize: fileSize))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            initialDelay: TimeSpan.FromMilliseconds(50),
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        var authorized = await session.AuthorizeCapture();
        Assert.True(authorized);

        Assert.True(File.Exists(opts.BeginSignalPath), "Begin signal file must exist after authorization");
        var writtenToken = File.ReadAllText(opts.BeginSignalPath);
        Assert.Equal(opts.BeginToken, writtenToken);

        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.DoesNotContain(opts.BeginToken, result.StderrTail);
    }

    [Fact]
    public async Task AuthorizeCapture_OnlySucceedsOnce()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            initialDelay: TimeSpan.FromMilliseconds(50),
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        Assert.True(await session.AuthorizeCapture());
        Assert.False(await session.AuthorizeCapture());
        Assert.False(await session.AuthorizeCapture());
    }

    [Fact]
    public async Task AuthorizeCapture_Concurrent_OnlyOneSucceeds()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            initialDelay: TimeSpan.FromMilliseconds(50),
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => session.AuthorizeCapture()))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(19, results.Count(r => !r));
        Assert.True(File.Exists(opts.BeginSignalPath));
        Assert.Equal(opts.BeginToken, File.ReadAllText(opts.BeginSignalPath));

        await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AuthorizeCapture_RaceWithDispose_NoResidualSignal()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o => o.ProcessTimeoutMs = 5000);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            var fake = new FakeWgcContinuousProcess(stdout,
                initialDelay: TimeSpan.FromMilliseconds(10),
                createOutputFile: true,
                outputFileSize: fileSize,
                outputFilePath: opts.OutputPath);
            fake.WaitForBeginSignalPath = opts.BeginSignalPath;
            var session = new WgcContinuousManagedSession(opts, fake);
            _disposables.Add(session);

            await session.StartAsync();

            // Race authorization against disposal. Polling disposes as soon as
            // the begin signal file appears, maximizing the chance of hitting
            // the window after write but before completion.
            var disposeTask = Task.Run(async () =>
            {
                for (int i = 0; i < 200; i++)
                {
                    if (File.Exists(opts.BeginSignalPath))
                    {
                        session.Dispose();
                        return;
                    }
                    await Task.Delay(5);
                }
                session.Dispose();
            });

            var authTask = Task.Run(() => session.AuthorizeCapture());
            await Task.WhenAll(authTask, disposeTask);
            await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(opts.BeginSignalPath), $"Residual begin signal on attempt {attempt}");
            Assert.False(File.Exists(opts.BeginSignalPath + ".tmp"), $"Residual begin tmp on attempt {attempt}");
        }
    }

    [Fact]
    public async Task AuthorizeCapture_Concurrent_WithControlPoint_ProvesLockNotHeld()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            initialDelay: TimeSpan.FromMilliseconds(50),
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        var writer = new ControllableSignalWriter();
        using var session = new WgcContinuousManagedSession(opts, fake, writer);
        _disposables.Add(session);

        await session.StartAsync();

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => session.AuthorizeCapture()))
            .ToArray();

        // Wait until the writer has entered the I/O point. At this moment the
        // session lock is definitely not held by the writer.
        await writer.IoPointTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The other 19 tasks must be able to observe that an authorization is
        // already in progress and return false without waiting for I/O.
        var sw = Stopwatch.StartNew();
        while (tasks.Count(t => t.IsCompleted) < 19 && sw.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(10);
        Assert.Equal(19, tasks.Count(t => t.IsCompleted));

        // Release the writer; the single owner completes successfully.
        writer.Release();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(19, results.Count(r => !r));
        Assert.True(File.Exists(opts.BeginSignalPath));
        Assert.Equal(opts.BeginToken, File.ReadAllText(opts.BeginSignalPath));
    }

    [Fact]
    public async Task AuthorizeCapture_CancelThenRetry_Succeeds()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            initialDelay: TimeSpan.FromMilliseconds(50),
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        var writer = new ControllableSignalWriter();
        using var session = new WgcContinuousManagedSession(opts, fake, writer);
        _disposables.Add(session);

        await session.StartAsync();

        using var cts = new CancellationTokenSource();
        var firstAuth = Task.Run(() => session.AuthorizeCapture(cts.Token));
        await writer.IoPointTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel the first authorization while it is inside the lock-free I/O.
        cts.Cancel();
        writer.Release();

        Assert.False(await firstAuth.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(WgcContinuousManagedSessionState.WaitingForAuthorization, session.State);

        // Second authorization with a fresh token must be able to win ownership.
        var retry = await session.AuthorizeCapture();
        Assert.True(retry);
        Assert.True(File.Exists(opts.BeginSignalPath));
        Assert.Equal(opts.BeginToken, File.ReadAllText(opts.BeginSignalPath));
    }

    [Fact]
    public async Task AuthorizeCapture_RaceWithDispose_DeterministicControlPoint_NoResidualSignal()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            initialDelay: TimeSpan.FromMilliseconds(50),
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        var writer = new ControllableSignalWriter();
        var session = new WgcContinuousManagedSession(opts, fake, writer);
        _disposables.Add(session);

        await session.StartAsync();

        var authTask = Task.Run(() => session.AuthorizeCapture());
        await writer.IoPointTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose while the writer is inside lock-free I/O. The session must be
        // able to acquire the state lock and mark completion.
        session.Dispose();

        // Now release the writer. It must clean up its just-written signal
        // rather than leaving it behind.
        writer.Release();
        await authTask.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
        Assert.False(File.Exists(opts.BeginSignalPath), "Begin signal must not be left after dispose race");
        Assert.False(File.Exists(opts.BeginSignalPath + ".tmp"), "Begin tmp must not be left after dispose race");
    }

    [Fact]
    public async Task AuthorizeCapture_TmpFileCleanupOnCancel()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o => o.ProcessTimeoutMs = 500);
        var writer = new TmpThenCanceledWriter();
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>(), ignoreStopSignal: true);
        using var session = new WgcContinuousManagedSession(opts, fake, writer);
        _disposables.Add(session);

        await session.StartAsync();

        using var cts = new CancellationTokenSource();
        var authTask = session.AuthorizeCapture(cts.Token);

        await writer.TmpWrittenTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(opts.BeginSignalPath + ".tmp"), "tmp file must exist before cancel");

        cts.Cancel();
        await authTask.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.False(File.Exists(opts.BeginSignalPath), "Begin signal must not exist after cancel");
        Assert.False(File.Exists(opts.BeginSignalPath + ".tmp"), "Begin tmp must be cleaned up after cancel");
        Assert.DoesNotContain(opts.BeginToken, result.StderrTail ?? string.Empty);
    }

    [Fact]
    public async Task AuthorizeCapture_LinkedCtsReleasedAfterRepeatedCancels()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>(), ignoreStopSignal: true);
        using var session = new WgcContinuousManagedSession(opts, fake, new FileAuthorizationSignalWriter());
        _disposables.Add(session);

        await session.StartAsync();
        Assert.Equal(WgcContinuousManagedSessionState.WaitingForAuthorization, session.State);

        for (int i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource();
            var authTask = session.AuthorizeCapture(cts.Token);
            cts.Cancel();

            Assert.False(await authTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await WaitForConditionAsync(() => session.ActiveAuthorizationLinkedCtsCountForTests == 0, TimeSpan.FromSeconds(5));
            Assert.Equal(0, session.ActiveAuthorizationLinkedCtsCountForTests);
            Assert.Equal(WgcContinuousManagedSessionState.WaitingForAuthorization, session.State);
        }
    }

    [Fact]
    public async Task AuthorizeCapture_DisposeBetweenReservationAndAttempt_CompletesOwnerAndCleansUp()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var writer = new ControllableSignalWriter();
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>(), ignoreStopSignal: true);
        using var session = new WgcContinuousManagedSession(opts, fake, writer);
        _disposables.Add(session);

        await session.StartAsync();

        var barrierTcs = new TaskCompletionSource();
        var disposeTask = Task.Run(async () =>
        {
            await barrierTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.Dispose();
        });

        session.AuthorizeOwnerReservedBarrierForTests = () => barrierTcs.TrySetResult();

        var authTask = session.AuthorizeCapture();
        await Task.WhenAll(authTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(5));

        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(await authTask);
        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);
        Assert.Equal(0, session.ActiveAuthorizationLinkedCtsCountForTests);
        Assert.False(File.Exists(opts.BeginSignalPath));
        Assert.False(File.Exists(opts.BeginSignalPath + ".tmp"));
    }

    [Fact]
    public async Task FileAuthorizationSignalWriter_MoveFailure_DeletesTmp_DoesNotDeleteForeignFinal()
    {
        var dir = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tmpPath = Path.Combine(dir, "begin.signal.tmp");
        var finalPath = Path.Combine(dir, "begin.signal");
        const string existingContent = "preexisting-final";
        File.WriteAllText(finalPath, existingContent);

        // Lock the destination file so File.Move cannot overwrite it.
        using var fs = new FileStream(finalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var writer = new FileAuthorizationSignalWriter();
        var thrown = await Record.ExceptionAsync(
            () => writer.WriteBeginTokenAsync(tmpPath, finalPath, "secret-token", CancellationToken.None));

        Assert.NotNull(thrown);
        Assert.False(File.Exists(tmpPath), "tmp must be deleted when this call did not commit the move");

        // Release the lock before reading back the foreign final file.
        fs.Dispose();
        Assert.Equal(existingContent, File.ReadAllText(finalPath));
    }

    [Theory]
    [InlineData("started")]
    [InlineData("progress")]
    [InlineData("ok")]
    public async Task EventBeforeAuthorization_Fails(string eventType)
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        string[] stdout = eventType switch
        {
            "started" => Started(recId, opts.OutputPath),
            "progress" => Progress(1, 100),
            "ok" => Ok(),
            _ => throw new ArgumentOutOfRangeException(nameof(eventType))
        };

        var fake = new FakeWgcContinuousProcess(stdout);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref firstFrameCount);

        await session.StartAsync();
        // Deliberately do not authorize.
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("event_before_authorization", result.FailureCategory);
        Assert.Equal(0, firstFrameCount);
    }

    // -----------------------------------------------------------------
    // Success / Stopped sequences
    // -----------------------------------------------------------------

    [Fact]
    public async Task SuccessfulSequence_ProducesSuccessSummary_AndExactlyOnceFirstFrame()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath)
            .Concat(Progress(1, 100, 50000))
            .Concat(Progress(150, 2500, 7500000))
            .Concat(Ok(300, 5000, fileSize))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        FirstFrameObservation? observed = null;
        session.FirstFrameObserved += ffo =>
        {
            Interlocked.Increment(ref firstFrameCount);
            observed = ffo;
        };

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.NotNull(result.Summary);
        Assert.Equal(ContinuousSessionState.Success, result.Summary!.State);
        Assert.True(result.FirstFrameObserved);
        Assert.Equal(1, firstFrameCount);
        Assert.NotNull(observed);
        Assert.Equal(1, observed!.FrameNumber);
        Assert.Equal(100, observed.OutTimeUs / 1000);
        Assert.True(result.OutputFileExists);
        Assert.Equal(fileSize, result.OutputFileSizeBytes);
    }

    [Fact]
    public async Task StoppedSequence_ProducesStoppedSummary()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 7500000L;
        var initial = Started(recId, opts.OutputPath)
            .Concat(Progress(1, 100))
            .Concat(Progress(150, 2500))
            .ToArray();
        var final = Stopped(150, 2500, fileSize);
        var fake = new FakeWgcContinuousProcess(initial, final,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();

        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            fake.Continue();
        });

        var stopped = await session.RequestStop();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped, "RequestStop must report true for a graceful Stopped result");
        Assert.Equal(WgcContinuousManagedSessionState.Stopped, result.State);
        Assert.Equal(ContinuousSessionState.Stopped, result.Summary!.State);
        Assert.True(result.StopRequestedByCaller);
        Assert.True(result.OutputFileExists);
        Assert.Equal(fileSize, result.OutputFileSizeBytes);
    }

    [Fact]
    public async Task RequestStop_CreatesStopSignal_AndCleansIt()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o => o.StopWaitTimeoutMs = 3000);
        var fileSize = 5000L;
        var initial = Started(recId, opts.OutputPath)
            .Concat(Progress(1, 100))
            .ToArray();
        var final = Stopped(1, 100, fileSize);
        var fake = new FakeWgcContinuousProcess(initial, final,
            autoContinueOnStopSignalPath: opts.StopSignalPath,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();

        Assert.False(File.Exists(opts.StopSignalPath), "Stop signal must not exist before RequestStop");

        var stopped = await session.RequestStop();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped);
        Assert.Equal(WgcContinuousManagedSessionState.Stopped, result.State);
        Assert.False(File.Exists(opts.StopSignalPath), "Stop signal must be cleaned after completion");
    }

    [Fact]
    public async Task RequestStop_ReturnsFalse_AfterNaturalSuccess()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);

        var stopped = await session.RequestStop();
        Assert.False(stopped, "RequestStop after natural Success must return false");
    }

    [Fact]
    public async Task RequestStop_ReturnsFalse_AfterFailure()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = Fail("encoding_error", "encoding_error");
        var fake = new FakeWgcContinuousProcess(stdout);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);

        var stopped = await session.RequestStop();
        Assert.False(stopped, "RequestStop after Failure must return false");
    }

    [Fact]
    public async Task RequestStop_ReturnsFalse_AfterCancellation()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = Started(recId, opts.OutputPath).Concat(Ok()).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout, ignoreStopSignal: true);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        session.Dispose();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Cancelled, result.State);

        var stopped = await session.RequestStop();
        Assert.False(stopped, "RequestStop after Cancellation must return false");
    }

    [Fact]
    public async Task RequestStop_StopSignalCreateFailed_ReturnsFalse()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        // Make the stop signal path a directory so File.WriteAllText fails.
        Directory.CreateDirectory(opts.StopSignalPath);

        var fileSize = 15000000L;
        var initial = Started(recId, opts.OutputPath);
        var final = Ok(fileSize: fileSize);
        var fake = new FakeWgcContinuousProcess(initial, final,
            waitForBeginSignalPath: opts.BeginSignalPath,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();

        var stopped = await session.RequestStop();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(stopped);
        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("lifecycle", result.FailurePhase);
        Assert.Equal("stop_signal_create_failed", result.FailureCategory);
    }

    [Fact]
    public async Task Stop_BeforeStarted_CompletesOnce()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = Fail("stopped-before-begin", "begin_not_authorized");
        var fake = new FakeWgcContinuousProcess(stdout);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        var stopTask = session.RequestStop();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.NotEqual("cancelled", result.FailureCategory);
        await stopTask;
    }

    [Fact]
    public async Task RepeatedStopAndDispose_Idempotent()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();

        var stop1 = session.RequestStop();
        var stop2 = session.RequestStop();
        var stop3 = session.RequestStop();

        await Task.WhenAll(stop1, stop2, stop3);
        session.Dispose();
        session.Dispose();

        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
    }

    // -----------------------------------------------------------------
    // Output authenticity
    // -----------------------------------------------------------------

    [Fact]
    public async Task Success_OK_MissingOutputFile_Fails()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = Started(recId, opts.OutputPath).Concat(Ok()).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout); // no output file
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("output_authenticity", result.FailurePhase);
        Assert.Equal("missing_output_file", result.FailureCategory);
    }

    [Fact]
    public async Task Success_OK_ZeroByteOutput_Fails()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = Started(recId, opts.OutputPath).Concat(Ok()).ToArray();
        CreatePlaceholderMp4(opts.OutputPath, 0);
        var fake = new FakeWgcContinuousProcess(stdout);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("output_authenticity", result.FailurePhase);
        Assert.Equal("empty_output_file", result.FailureCategory);
    }

    [Fact]
    public async Task Success_OK_NonZeroExitCode_Fails()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        CreatePlaceholderMp4(opts.OutputPath, fileSize);
        var fake = new FakeWgcContinuousProcess(stdout, exitCode: 1);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("output_authenticity", result.FailurePhase);
        Assert.Equal("non_zero_exit_code", result.FailureCategory);
    }

    [Fact]
    public async Task Success_OK_OutputPathMismatch_Fails()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var otherPath = Path.Combine(_tempDir, "other.mp4");
        var fileSize = 15000000L;
        var stdout = Started(recId, otherPath).Concat(Ok(fileSize: fileSize)).ToArray();
        CreatePlaceholderMp4(otherPath, fileSize);
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: otherPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("output_authenticity", result.FailurePhase);
        Assert.Equal("output_path_mismatch", result.FailureCategory);
    }

    [Fact]
    public async Task Success_OK_FileSizeMismatch_Fails()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        CreatePlaceholderMp4(opts.OutputPath, fileSize - 1);
        var fake = new FakeWgcContinuousProcess(stdout);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("output_authenticity", result.FailurePhase);
        Assert.Equal("file_size_mismatch", result.FailureCategory);
    }

    [Fact]
    public async Task Success_OK_BytesWrittenMismatch_Fails()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath)
            .Concat(new[]
            {
                "RESULT: OK",
                "FramesCaptured: 300",
                "FramesDropped: 0",
                "DurationMs: 5000",
                $"BytesWritten: {fileSize}",
                "Width: 1920",
                "Height: 1080",
                ""
            })
            .ToArray();
        CreatePlaceholderMp4(opts.OutputPath, fileSize - 1);
        var fake = new FakeWgcContinuousProcess(stdout);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("output_authenticity", result.FailurePhase);
        Assert.Equal("bytes_written_mismatch", result.FailureCategory);
    }

    [Fact]
    public async Task Success_OK_InvalidSummaryOutputPath_FailsWithoutHanging()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var invalidPath = Path.Combine(_tempDir, "path\0illegal.mp4");
        var fileSize = 15000000L;
        var stdout = Started(recId, invalidPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("output_authenticity", result.FailurePhase);
        Assert.Equal("invalid_output_path", result.FailureCategory);
    }

    [Fact]
    public async Task FinalizeAsync_BuildResultException_SynchronizesFailedState()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            initialDelay: TimeSpan.FromMilliseconds(50),
            exitDelay: TimeSpan.FromMilliseconds(100),
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);
        session.ThrowFromBuildResultForTests = true;

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal(WgcContinuousManagedSessionState.Failed, session.State);
        Assert.Equal("lifecycle", result.FailurePhase);
        Assert.Equal("finalize_exception", result.FailureCategory);
    }

    // -----------------------------------------------------------------
    // Failure reasons / lifecycle
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("window_closed")]
    [InlineData("window_minimized")]
    [InlineData("size_changed")]
    public async Task HelperLifecycleFail_PreservesSpecificReasonAcrossExitArbitration(string reason)
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = new[]
        {
            "RESULT: STARTED",
            $"RecordingId: {recId}",
            $"Output: {opts.OutputPath}",
            "Container: mp4",
            "Codec: h264",
            "Fps: 30",
            "Width: 1920",
            "Height: 1080",
            "CaptureMethod: WGC_D3D11_FRAME_STREAM",
            "",
            "RESULT: FAIL",
            $"ErrorCode: {reason}",
            $"Reason: {reason}",
            "FramesCaptured: 8",
            "BytesWritten: 1024",
            ""
        };
        var fake = new FakeWgcContinuousProcess(
            stdout,
            exitCode: 1,
            waitForBeginSignalPath: opts.BeginSignalPath);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal(ContinuousSessionState.Failed, result.Summary!.State);
        Assert.Equal(reason, result.Summary.ErrorCode);
        Assert.Equal(reason, result.Summary.GetStopReasonForEvidence());
        Assert.Equal(reason, result.FailureCategory);
    }

    [Fact]
    public async Task Helper_NonZeroExit_NoTerminal_Fails()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>(), exitCode: 1);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal(ContinuousSessionState.MalformedSequence, result.Summary!.State);
        Assert.Equal("ipc_parser", result.FailurePhase);
        Assert.Equal("missing_or_malformed_terminal_event", result.FailureCategory);
    }

    [Fact]
    public async Task Helper_MalformedSequence_Fails()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = new[]
        {
            "RESULT: PROGRESS",
            "FramesCaptured: 10",
            "ElapsedMs: 100",
            "",
            "RESULT: OK",
            "FramesCaptured: 10"
        };
        var fake = new FakeWgcContinuousProcess(stdout);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref firstFrameCount);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal(ContinuousSessionState.MalformedSequence, result.Summary!.State);
        Assert.Contains(result.Summary.ValidationErrors, e => e.Contains("PROGRESS event before STARTED"));
        Assert.Equal(0, firstFrameCount);
    }

    [Fact]
    public async Task Helper_IgnoresStop_KillsTree()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o =>
        {
            o.ProcessTimeoutMs = 500;
            o.StopWaitTimeoutMs = 200;
        });
        var stdout = Started(recId, opts.OutputPath)
            .Concat(Progress(1, 100))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout, ignoreStopSignal: true);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("lifecycle", result.FailurePhase);
        Assert.Equal("process_timeout", result.FailureCategory);
        Assert.True(fake.WasKilled, "Process tree must be killed when helper ignores stop");
    }

    [Fact]
    public async Task ProcessStartFailed_RetainsPhaseCategory()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fake = new ThrowingStartProcess(new InvalidOperationException("boom"));
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("lifecycle", result.FailurePhase);
        Assert.Equal("process_start_failed", result.FailureCategory);
    }

    [Fact]
    public async Task AuthorizeWriteFailed_RetainsPhaseCategory()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        // Make begin signal path a directory so writing the token fails.
        Directory.CreateDirectory(opts.BeginSignalPath);

        var initial = Started(recId, opts.OutputPath);
        var final = Ok();
        var fake = new FakeWgcContinuousProcess(initial, final,
            waitForBeginSignalPath: opts.BeginSignalPath);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        var authorized = await session.AuthorizeCapture();
        Assert.False(authorized);

        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("lifecycle", result.FailurePhase);
        Assert.Equal("authorize_write_failed", result.FailureCategory);
    }

    [Fact]
    public async Task Failed_AfterDrainOk_RemainsFailed()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o => o.ProcessTimeoutMs = 500);
        var fileSize = 15000000L;

        // The helper emits a valid STARTED+OK after authorization but refuses
        // to exit, so the watcher records a process_timeout lifecycle failure.
        // During stdout drain the OK event is still parsed. The final state
        // must remain Failed and keep the original lifecycle category.
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(300, 5000, fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            waitForBeginSignalPath: opts.BeginSignalPath,
            ignoreStopSignal: true,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("lifecycle", result.FailurePhase);
        Assert.Equal("process_timeout", result.FailureCategory);
    }

    // -----------------------------------------------------------------
    // Input bounds
    // -----------------------------------------------------------------

    [Fact]
    public async Task Stdout_EventFlood_TriggersLimit()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var flood = Enumerable.Range(0, 10020)
            .SelectMany(i => new[]
            {
                "RESULT: PROGRESS",
                $"FramesCaptured: {i + 1}",
                "ElapsedMs: 100",
                ""
            })
            .ToArray();
        var fake = new FakeWgcContinuousProcess(flood);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("max_stdout_events_exceeded", result.FailureCategory);
    }

    [Fact]
    public async Task Stdout_LongLine_TriggersLimit()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = new[]
        {
            "RESULT: PROGRESS",
            $"FramesCaptured: {new string('0', 20000)}",
            "ElapsedMs: 100",
            ""
        };
        var fake = new FakeWgcContinuousProcess(stdout);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("max_stdout_line_length_exceeded", result.FailureCategory);
    }

    [Fact]
    public async Task Stdout_LongBlock_TriggersLimit()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var lines = new List<string> { "RESULT: PROGRESS" };
        lines.AddRange(Enumerable.Range(0, 1005).Select(_ => "Padding: value"));
        lines.Add("ElapsedMs: 100");
        lines.Add("");
        var fake = new FakeWgcContinuousProcess(lines);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("max_lines_per_event_block_exceeded", result.FailureCategory);
    }

    [Fact]
    public async Task Helper_HugeStderr_MemoryBounded()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var hugeStderr = Enumerable.Range(0, 10000)
            .Select(i => $"diagnostic line {i} with enough padding to exceed any small buffer limit quickly")
            .ToArray();
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            stderr: hugeStderr,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.True(result.StderrTail.Length <= 40000, "Stderr tail must be bounded");
        Assert.Contains("diagnostic line 9999", result.StderrTail);
        Assert.DoesNotContain("diagnostic line 0", result.StderrTail);
    }

    [Fact]
    public async Task Stdout_HugeUnboundedLine_TriggersLimit()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o => o.ProcessTimeoutMs = 5000);
        var fake = new HugeStdoutProcess(5L * 1024 * 1024);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("max_stdout_line_length_exceeded", result.FailureCategory);
    }

    [Fact]
    public async Task Stderr_HugeUnboundedLine_TailBounded()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fake = new HugeStderrProcess(5L * 1024 * 1024, opts.OutputPath)
        {
            WaitForBeginSignalPath = opts.BeginSignalPath
        };
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        CreatePlaceholderMp4(opts.OutputPath, 100);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.True(result.StderrTail.Length <= 32768, "Stderr tail must be bounded to MaxStderrChars");
        Assert.True(result.StderrTail.Length > 0);
        Assert.All(result.StderrTail, c => Assert.Equal('y', c));
    }

    [Fact]
    public async Task Stdout_CrlfAcrossChunks_AndEofNoNewline_Parses()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        opts.OutputPath = Path.Combine(_tempDir, "crlf-out.mp4");
        File.WriteAllText(opts.HelperExePath, "fake");

        // CRLF-delimited events with no trailing newline after the final OK.
        var stdoutText =
            "RESULT: STARTED\r\n" +
            $"RecordingId: {recId}\r\n" +
            $"Output: {opts.OutputPath}\r\n" +
            "Container: mp4\r\n" +
            "Codec: h264\r\n" +
            "Fps: 30\r\n" +
            "Width: 1920\r\n" +
            "Height: 1080\r\n" +
            "CaptureMethod: WGC_D3D11_FRAME_STREAM\r\n" +
            "\r\n" +
            "RESULT: OK\r\n" +
            "FramesCaptured: 1\r\n" +
            "DurationMs: 1000\r\n" +
            "FileSize: 100 bytes\r\n" +
            "Width: 1920\r\n" +
            "Height: 1080";
        var bytes = Encoding.UTF8.GetBytes(stdoutText);

        // Chunk size 16 splits the first CRLF across chunks ("RESULT: STARTED\r" / "\n...").
        var fake = new PreciseChunkStdoutProcess(bytes, 16, autoExitDelay: TimeSpan.FromMilliseconds(50));
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        CreatePlaceholderMp4(opts.OutputPath, 100);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.NotNull(result.Summary);
        Assert.Equal(ContinuousSessionState.Success, result.Summary!.State);
        Assert.Equal(recId, result.Summary.RecordingId);
    }

    // -----------------------------------------------------------------
    // Misc
    // -----------------------------------------------------------------

    [Fact]
    public async Task NegativeDisplayCoordinates_BuildsCorrectArgumentList()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o =>
        {
            o.DisplayX = -1920;
            o.DisplayY = -1080;
            o.DisplayWidth = 2560;
            o.DisplayHeight = 1440;
            o.DurationMs = 2000;
            o.Fps = 60;
        });
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath).Concat(Ok(fileSize: fileSize)).ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();

        Assert.NotNull(fake.CapturedArguments);
        var args = fake.CapturedArguments!.ToList();
        Assert.Equal("--capture-continuous-display", args[0]);
        Assert.Equal("--display-bounds", args[1]);
        Assert.Equal("-1920,-1080,2560,1440", args[2]);
        Assert.Equal("--duration-ms", args[args.IndexOf("--duration-ms")]);
        Assert.Equal("2000", args[args.IndexOf("--duration-ms") + 1]);
        Assert.Equal("60", args[args.IndexOf("--fps") + 1]);
        Assert.Equal(opts.BeginSignalPath, args[args.IndexOf("--begin-signal") + 1]);
        Assert.Equal(opts.StopSignalPath, args[args.IndexOf("--stop-signal") + 1]);
        Assert.Equal("--i-understand-this-captures-screen", args[^1]);

        await session.AuthorizeCapture();
        await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WindowTarget_BuildsWindowArgumentAndPreservesHwndToken()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o =>
        {
            o.TargetKind = WgcContinuousTargetKind.Window;
            o.WindowHandle = (nint)0x1234;
            o.DisplayWidth = 0;
            o.DisplayHeight = 0;
            o.DurationMs = 2000;
        });
        var stdout = Started(recId, opts.OutputPath)
            .Select(line => line.Replace(
                "WGC_D3D11_FRAME_STREAM",
                "WGC_D3D11_WINDOW_FRAME_STREAM",
                StringComparison.Ordinal))
            .Concat(Ok(fileSize: 15000000L))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: 15000000L,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        await session.StartAsync();

        var args = fake.CapturedArguments!.ToList();
        Assert.Equal("--capture-continuous-window", args[0]);
        Assert.Equal("--window-hwnd", args[1]);
        Assert.Equal("0x1234", args[2]);
        Assert.DoesNotContain("--display-bounds", args);

        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(
            result.State == WgcContinuousManagedSessionState.Success,
            $"state={result.State}; phase={result.FailurePhase}; category={result.FailureCategory}; reason={result.Summary?.Reason}; errors={string.Join(" | ", result.Summary?.ValidationErrors ?? new List<string>())}");
        Assert.Equal("WGC_D3D11_WINDOW_FRAME_STREAM", result.Summary!.CaptureMethod);
    }

    [Fact]
    public void WindowTarget_ZeroHwndRejectedBeforeProcessStart()
    {
        var opts = CreateOptions($"rec_{Guid.NewGuid():N}", o =>
        {
            o.TargetKind = WgcContinuousTargetKind.Window;
            o.WindowHandle = nint.Zero;
        });
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>());
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        var ex = Assert.Throws<ArgumentException>(() => session.StartAsync().GetAwaiter().GetResult());
        Assert.Contains("Window handle", ex.Message);
        Assert.Equal(0, fake.StartInvocationCount);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(15000)]
    public void Duration_OutOfRange_Rejected(int durationMs)
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId, o => o.DurationMs = durationMs);
        var fake = new FakeWgcContinuousProcess(Array.Empty<string>());
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        var ex = Assert.Throws<ArgumentException>(() => session.StartAsync().GetAwaiter().GetResult());
        Assert.Contains("Duration", ex.Message);
    }

    [Fact]
    public async Task RealProcessTreeFixture_Timeout_KillsEntireTree()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var childPidFile = Path.Combine(_tempDir, $"{recId}.child.pid");
        var opts = new WgcContinuousSessionOptions
        {
            HelperExePath = Path.Combine(_tempDir, "wgc-native-helper.exe"),
            RecordingId = recId,
            DisplayX = 0,
            DisplayY = 0,
            DisplayWidth = 1920,
            DisplayHeight = 1080,
            OutputPath = Path.Combine(_tempDir, $"{recId}.mp4"),
            DurationMs = 1000,
            Fps = 30,
            BeginSignalPath = Path.Combine(_tempDir, $"{recId}.begin.signal"),
            BeginToken = "tok",
            BeginTimeoutMs = 100,
            StopSignalPath = Path.Combine(_tempDir, $"{recId}.stop.signal"),
            ProcessTimeoutMs = 2000,
            StopWaitTimeoutMs = 200
        };
        File.WriteAllText(opts.HelperExePath, "fake");

        var fixture = new RealProcessTreeFixtureProcess(childPidFile);
        var session = new WgcContinuousManagedSession(opts, fixture);
        _disposables.Add(session);
        _disposables.Add(fixture);

        int? observedChildPid = null;
        try
        {
            await session.StartAsync();

            // Prove both the fixture root process and its child ping were alive
            // before the session timeout fired. This ensures the post-timeout
            // "process is gone" assertions actually validate a kill, not a
            // never-started process.
            var aliveSw = Stopwatch.StartNew();
            while (fixture.ChildPid == null && aliveSw.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50);

            Assert.NotNull(fixture.ChildPid);
            Assert.NotEqual(0, fixture.Id);
            observedChildPid = fixture.ChildPid;

            try
            {
                var root = Process.GetProcessById(fixture.Id);
                root.Refresh();
                Assert.False(root.HasExited, "Root process must be alive before timeout");
            }
            catch (ArgumentException)
            {
                Assert.Fail("Root process missing before timeout");
            }

            try
            {
                var child = Process.GetProcessById(fixture.ChildPid.Value);
                child.Refresh();
                Assert.False(child.HasExited, "Child ping must be alive before timeout");
            }
            catch (ArgumentException)
            {
                Assert.Fail("Child ping missing before timeout");
            }

            var sw = Stopwatch.StartNew();
            var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(15));
            sw.Stop();

            _output.WriteLine($"Real process timeout test elapsed: {sw.Elapsed}, state: {result.State}, exit: {result.ExitCode}");

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), "Timeout must kill the process well before the fixture finishes");
            Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
            Assert.Equal("lifecycle", result.FailurePhase);
            Assert.Equal("process_timeout", result.FailureCategory);
            Assert.False(File.Exists(opts.BeginSignalPath));
            Assert.False(File.Exists(opts.StopSignalPath));

            // The fixture root process must be gone.
            if (fixture.Id != 0)
            {
                try
                {
                    var root = Process.GetProcessById(fixture.Id);
                    root.Refresh();
                    Assert.True(root.HasExited, "Root process must be dead after timeout");
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            }

            // The child ping process must also be gone.
            try
            {
                var child = Process.GetProcessById(observedChildPid.Value);
                child.Refresh();
                Assert.True(child.HasExited, "Child process must be dead after timeout");
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        finally
        {
            // Whatever the session did or failed to do, the fixture must not
            // leak real powershell/ping processes into later test runs.
            fixture.KillOwnedProcessTree();
        }

        // Final leak guard: no fixture-owned process may survive the test.
        if (fixture.Id != 0)
        {
            try
            {
                var root = Process.GetProcessById(fixture.Id);
                root.Refresh();
                Assert.True(root.HasExited, "Root process must not survive the test");
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        if (observedChildPid.HasValue)
        {
            try
            {
                var child = Process.GetProcessById(observedChildPid.Value);
                child.Refresh();
                Assert.True(child.HasExited, "Child ping must not survive the test");
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
    }

    // -----------------------------------------------------------------
    // Explicit FIRST_FRAME event tests
    // -----------------------------------------------------------------

    private static string[] FirstFrame(long frameNumber, long elapsedMs) => new[]
    {
        "RESULT: FIRST_FRAME",
        "Stage: Capturing",
        $"FrameNumber: {frameNumber}",
        $"ElapsedMs: {elapsedMs}",
        "" // blank-line event separator
    };

    [Fact]
    public async Task ExplicitFirstFrame_RaisesExactlyOnce_AndSuppressesProgressFallback()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath)
            .Concat(FirstFrame(1, 17))
            .Concat(Progress(1, 100, 50000))
            .Concat(Progress(150, 2500, 7500000))
            .Concat(Ok(300, 5000, fileSize))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        FirstFrameObservation? observed = null;
        session.FirstFrameObserved += ffo =>
        {
            Interlocked.Increment(ref firstFrameCount);
            observed = ffo;
        };

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.True(result.FirstFrameObserved);
        Assert.Equal(1, firstFrameCount);
        Assert.NotNull(observed);
        // The explicit event must win over the legacy progress fallback and
        // must carry truthful, non-fabricated evidence.
        Assert.Equal("wgc_continuous_first_frame", observed!.EvidenceKind);
        Assert.Equal(1, observed.FrameNumber);
        Assert.Equal(17_000, observed.OutTimeUs);
        Assert.Equal(0, observed.TotalSizeBytes);
        Assert.NotNull(result.Summary);
        Assert.True(result.Summary!.FirstFrameObserved);
        Assert.Equal(1, result.Summary.FirstFrameNumber);
        Assert.Equal(17, result.Summary.FirstFrameElapsedMs);
        // The explicit event must not change encoded frame counts.
        Assert.Equal(300, result.Summary.FramesCaptured);
    }

    [Fact]
    public async Task ExplicitFirstFrame_ImmediatelyAfterStarted_IsPublishedBeforeProgressTick()
    {
        // Static single-frame case: FIRST_FRAME fires while FramesCaptured is
        // still zero and the final outcome encodes exactly one frame.
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 900000L;
        var stdout = Started(recId, opts.OutputPath)
            .Concat(FirstFrame(1, 0))
            .Concat(Ok(1, 10000, fileSize))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref firstFrameCount);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.Equal(1, firstFrameCount);
        Assert.Equal(1, result.Summary!.FramesCaptured);
        Assert.Equal(0, result.Summary.FirstFrameElapsedMs);
    }

    [Fact]
    public async Task LegacyProgressFallback_RaisesExactlyOnce_WithLegacyEvidenceKind()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath)
            .Concat(Progress(1, 100, 50000))
            .Concat(Progress(150, 2500, 7500000))
            .Concat(Ok(300, 5000, fileSize))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        FirstFrameObservation? observed = null;
        session.FirstFrameObserved += ffo =>
        {
            Interlocked.Increment(ref firstFrameCount);
            observed = ffo;
        };

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Success, result.State);
        Assert.Equal(1, firstFrameCount);
        Assert.NotNull(observed);
        Assert.Equal("wgc_continuous_progress", observed!.EvidenceKind);
        Assert.False(result.Summary!.FirstFrameObserved,
            "legacy fallback must not be recorded as explicit FIRST_FRAME evidence");
    }

    [Fact]
    public async Task FirstFrameBeforeAuthorization_IsProtocolViolation_NoObservation()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = Started(recId, opts.OutputPath)
            .Concat(FirstFrame(1, 5))
            .Concat(Ok())
            .ToArray();

        var fake = new FakeWgcContinuousProcess(stdout);
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref firstFrameCount);

        await session.StartAsync();
        // Deliberately do not authorize: any capture event before authorization
        // is a consent-gate protocol violation, including FIRST_FRAME.
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("event_before_authorization", result.FailureCategory);
        Assert.Equal(0, firstFrameCount);
    }

    // -----------------------------------------------------------------
    // Malformed live FIRST_FRAME trust-boundary tests (Task 196B)
    // -----------------------------------------------------------------

    private static string[] FirstFrameRaw(params string[] fieldLines)
    {
        var lines = new List<string> { "RESULT: FIRST_FRAME" };
        lines.AddRange(fieldLines);
        lines.Add(""); // blank-line event separator
        return lines.ToArray();
    }

    [Theory]
    [InlineData("missing_frame_number", new[] { "Stage: Capturing", "ElapsedMs: 10" })]
    [InlineData("nonnumeric_frame_number", new[] { "Stage: Capturing", "FrameNumber: one", "ElapsedMs: 10" })]
    [InlineData("zero_frame_number", new[] { "Stage: Capturing", "FrameNumber: 0", "ElapsedMs: 10" })]
    [InlineData("negative_frame_number", new[] { "Stage: Capturing", "FrameNumber: -1", "ElapsedMs: 10" })]
    [InlineData("missing_elapsed", new[] { "Stage: Capturing", "FrameNumber: 1" })]
    [InlineData("nonnumeric_elapsed", new[] { "Stage: Capturing", "FrameNumber: 1", "ElapsedMs: soon" })]
    [InlineData("negative_elapsed", new[] { "Stage: Capturing", "FrameNumber: 1", "ElapsedMs: -5" })]
    [InlineData("invalid_stage", new[] { "Stage: Finalizing", "FrameNumber: 1", "ElapsedMs: 10" })]
    [InlineData("missing_stage", new[] { "FrameNumber: 1", "ElapsedMs: 10" })]
    public async Task MalformedFirstFrame_LiveSession_ProtocolFailure_NoObservation_NoFallbackRescue(
        string caseName, string[] firstFrameFields)
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = Started(recId, opts.OutputPath)
            .Concat(FirstFrameRaw(firstFrameFields))
            // A later PROGRESS with FramesCaptured > 0 must NOT rescue the
            // session or produce a fallback observation after the malformed
            // explicit event failed the trust boundary.
            .Concat(Progress(1, 100, 50000))
            .Concat(Ok())
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref firstFrameCount);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.State == WgcContinuousManagedSessionState.Failed,
            $"{caseName}: malformed FIRST_FRAME must fail the session, got {result.State}");
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("first_frame_invalid", result.FailureCategory);
        Assert.Equal(0, firstFrameCount);
        Assert.False(result.FirstFrameObserved,
            $"{caseName}: malformed FIRST_FRAME must not produce a public first-frame result");
    }

    [Fact]
    public async Task FirstFrameBeforeStarted_LiveSession_ProtocolFailure_NoObservation()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        // FIRST_FRAME arrives after authorization but before STARTED.
        var stdout = FirstFrame(1, 5)
            .Concat(Started(recId, opts.OutputPath))
            .Concat(Ok())
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref firstFrameCount);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("first_frame_before_started", result.FailureCategory);
        Assert.Equal(0, firstFrameCount);
        Assert.False(result.FirstFrameObserved);
    }

    [Fact]
    public async Task FirstFrameAfterTerminal_LiveSession_ProtocolFailure_NoNewObservation()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var fileSize = 15000000L;
        var stdout = Started(recId, opts.OutputPath)
            .Concat(FirstFrame(1, 10))
            .Concat(Ok(300, 5000, fileSize))
            // A FIRST_FRAME after the terminal event must be rejected, and the
            // already-published evidence must not be re-published.
            .Concat(FirstFrame(2, 4000))
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout,
            createOutputFile: true,
            outputFileSize: fileSize,
            outputFilePath: opts.OutputPath);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        int firstFrameCount = 0;
        session.FirstFrameObserved += _ => Interlocked.Increment(ref firstFrameCount);

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("first_frame_after_terminal", result.FailureCategory);
        // The valid pre-terminal event was published exactly once; the
        // post-terminal duplicate added nothing.
        Assert.Equal(1, firstFrameCount);
    }

    [Fact]
    public async Task DuplicateFirstFrame_LiveSession_ProtocolFailure_PublishedExactlyOnce()
    {
        var recId = $"rec_{Guid.NewGuid():N}";
        var opts = CreateOptions(recId);
        var stdout = Started(recId, opts.OutputPath)
            .Concat(FirstFrame(1, 10))
            .Concat(FirstFrame(2, 20))
            .Concat(Ok())
            .ToArray();
        var fake = new FakeWgcContinuousProcess(stdout);
        fake.WaitForBeginSignalPath = opts.BeginSignalPath;
        using var session = new WgcContinuousManagedSession(opts, fake);
        _disposables.Add(session);

        var observed = new List<FirstFrameObservation>();
        session.FirstFrameObserved += ffo => { lock (observed) observed.Add(ffo); };

        await session.StartAsync();
        await session.AuthorizeCapture();
        var result = await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WgcContinuousManagedSessionState.Failed, result.State);
        Assert.Equal("protocol", result.FailurePhase);
        Assert.Equal("duplicate_first_frame", result.FailureCategory);
        lock (observed)
        {
            Assert.Single(observed);
            Assert.Equal(1, observed[0].FrameNumber);
        }
    }

    // -----------------------------------------------------------------
    // Supporting fakes
    // -----------------------------------------------------------------

    private sealed class ThrowingStartProcess : IWgcContinuousProcess
    {
        private readonly Exception _exception;

        public int Id => 0;
        public bool HasExited => true;
        public int ExitCode => -1;
        public Stream StandardOutputStream => Stream.Null;
        public Stream StandardErrorStream => Stream.Null;

        public ThrowingStartProcess(Exception exception) => _exception = exception;

        public void Start(string fileName, IReadOnlyList<string> argumentList)
            => throw _exception;

        public void KillEntireTree() { }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Dispose() { }
    }
}
