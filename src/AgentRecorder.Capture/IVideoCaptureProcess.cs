using System;
using System.Diagnostics;
using System.IO;

namespace AgentRecorder.Capture;

/// <summary>
/// Small process seam for the FFmpeg video worker. It keeps lifecycle tests
/// deterministic without launching a real capture process.
/// </summary>
internal interface IVideoCaptureProcess : IDisposable
{
    event DataReceivedEventHandler? ErrorDataReceived;

    ProcessStartInfo StartInfo { get; }
    StreamReader StandardOutput { get; }
    StreamWriter StandardInput { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    bool ErrorStreamClosed { get; }

    bool Start();
    void BeginErrorReadLine();
    bool WaitForExit(int milliseconds);
    bool WaitForExit(TimeSpan timeout);
    void Kill(bool entireProcessTree);
}

internal sealed class SystemVideoCaptureProcess : IVideoCaptureProcess
{
    private readonly Process _process;
    private volatile bool _errorStreamClosed;

    public SystemVideoCaptureProcess(ProcessStartInfo startInfo)
    {
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                _errorStreamClosed = true;
        };
    }

    public event DataReceivedEventHandler? ErrorDataReceived
    {
        add => _process.ErrorDataReceived += value;
        remove => _process.ErrorDataReceived -= value;
    }

    public ProcessStartInfo StartInfo => _process.StartInfo;
    public StreamReader StandardOutput => _process.StandardOutput;
    public StreamWriter StandardInput => _process.StandardInput;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public bool ErrorStreamClosed => _errorStreamClosed;

    public bool Start() => _process.Start();
    public void BeginErrorReadLine() => _process.BeginErrorReadLine();
    public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
    public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit(timeout);
    public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);
    public void Dispose() => _process.Dispose();
}
