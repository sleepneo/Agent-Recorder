using System.Diagnostics;
using System.Text;

namespace AgentRecorder.Capture;

/// <summary>
/// Narrow, testable abstraction over a long-running helper process used by
/// <see cref="WgcContinuousManagedSession"/>. The real implementation wraps
/// <see cref="System.Diagnostics.Process"/>; tests supply a fake that yields
/// stdout/stderr byte streams and controls exit timing.
/// </summary>
internal interface IWgcContinuousProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }

    /// <summary>
    /// Raw UTF-8 stdout stream. The session performs its own bounded decoding
    /// so that a misbehaving helper cannot force unbounded line allocation.
    /// </summary>
    Stream StandardOutputStream { get; }

    /// <summary>
    /// Raw UTF-8 stderr stream. The session performs its own bounded decoding
    /// so that a misbehaving helper cannot force unbounded line allocation.
    /// </summary>
    Stream StandardErrorStream { get; }

    void Start(string fileName, IReadOnlyList<string> argumentList);
    void KillEntireTree();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Production implementation of <see cref="IWgcContinuousProcess"/> using
/// <see cref="System.Diagnostics.Process"/>.
/// </summary>
internal sealed class RealWgcContinuousProcess : IWgcContinuousProcess
{
    private Process? _process;
    private readonly object _lock = new();

    public int Id
    {
        get
        {
            lock (_lock)
            {
                try { return _process?.Id ?? -1; }
                catch { return -1; }
            }
        }
    }

    public bool HasExited
    {
        get
        {
            lock (_lock)
            {
                try { return _process?.HasExited ?? false; }
                catch { return false; }
            }
        }
    }

    public int ExitCode
    {
        get
        {
            lock (_lock)
            {
                try { return _process?.ExitCode ?? -1; }
                catch { return -1; }
            }
        }
    }

    public Stream StandardOutputStream => _process?.StandardOutput.BaseStream ?? Stream.Null;
    public Stream StandardErrorStream => _process?.StandardError.BaseStream ?? Stream.Null;

    public void Start(string fileName, IReadOnlyList<string> argumentList)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Helper executable path must be provided.", nameof(fileName));
        if (argumentList == null)
            throw new ArgumentNullException(nameof(argumentList));

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in argumentList)
            psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        lock (_lock) _process = process;
    }

    public void KillEntireTree()
    {
        lock (_lock)
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { /* best effort */ }
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        Process? proc;
        lock (_lock) proc = _process;
        if (proc == null)
            return;
        await proc.WaitForExitAsync(cancellationToken);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _process?.Dispose(); }
            catch { /* best effort */ }
            _process = null;
        }
    }
}
