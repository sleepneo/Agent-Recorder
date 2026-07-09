using System.Diagnostics;
using System.Threading;

namespace AgentRecorder.Capture;

/// <summary>
/// Default IWgcHelperProcessRunner implementation — executes the real
/// helper process via Process.Start, captures stdout/stderr text, and
/// returns with the configured timeout.
///
/// Caller responsible for process execution and for keeping this
/// implementation isolated from the main recording pipeline until the
/// WGC backend is ready.
/// </summary>
public sealed class WgcHelperProcessRunner : IWgcHelperProcessRunner
{
    public WgcHelperProcessResult Run(
        string fileName,
        IReadOnlyList<string> argumentList,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Helper executable path must be provided.", nameof(fileName));
        if (argumentList == null)
            throw new ArgumentNullException(nameof(argumentList));

        using var process = new Process();
        var psi = process.StartInfo;
        psi.FileName = fileName;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.ErrorDialog = false;
        foreach (var a in argumentList) psi.ArgumentList.Add(a);

        process.Start();

        // Prefer ReadToEndAsync to respect the cancellation token
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        int actualTimeout = timeoutMs <= 0 ? Timeout.Infinite : timeoutMs;
        bool exited = process.WaitForExit(actualTimeout);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new WgcHelperProcessResult
            {
                ExitCode = -1,
                StandardOutput = GetResultSafely(stdoutTask) ?? "",
                StandardError = (GetResultSafely(stderrTask) ?? "") + " [.NET-side: WgcHelperProcessRunner timed out]",
            };
        }

        // Give ReadToEndAsync a chance to drain after the process exits
        var drain = Task.WhenAll(stdoutTask, stderrTask);
        try
        {
            drain.Wait(Math.Min(5000, Math.Max(actualTimeout, 1000)), cancellationToken);
        }
        catch { /* best effort */ }

        return new WgcHelperProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = GetResultSafely(stdoutTask) ?? "",
            StandardError = GetResultSafely(stderrTask) ?? "",
        };
    }

    private static string? GetResultSafely(Task<string> task)
    {
        if (task == null) return null;
        if (task.IsCompletedSuccessfully) return task.Result;
        try
        {
            if (task.Wait(200)) return task.Result;
        }
        catch { /* fall through */ }
        return null;
    }
}
