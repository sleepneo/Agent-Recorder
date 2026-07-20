using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Test seam for running external processes. Production uses
/// <see cref="ExternalProcessRunner"/>; tests inject deterministic fakes.
/// </summary>
public interface IExternalProcessRunner
{
    /// <summary>
    /// Runs a process with the given executable and argument list.
    /// </summary>
    /// <param name="fileName">Full path to the executable.</param>
    /// <param name="argumentList">Arguments passed via ArgumentList.</param>
    /// <param name="timeout">Maximum time to wait for the process.</param>
    /// <param name="captureStderr">Whether to capture a limited stderr excerpt.</param>
    /// <param name="stderrEncoding">
    /// Optional encoding used to decode captured stderr. When omitted the
    /// process uses the system default code page.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Exit code, stderr excerpt (if captured), and whether the process timed out.
    /// </returns>
    Task<ExternalProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> argumentList,
        System.TimeSpan timeout,
        bool captureStderr = true,
        Encoding? stderrEncoding = null,
        CancellationToken cancellationToken = default);
}

public sealed class ExternalProcessResult
{
    public int ExitCode { get; }
    public bool TimedOut { get; }
    public string Stderr { get; }

    public ExternalProcessResult(int exitCode, bool timedOut, string stderr)
    {
        ExitCode = exitCode;
        TimedOut = timedOut;
        Stderr = stderr ?? "";
    }
}
