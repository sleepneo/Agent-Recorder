using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Default IWgcHelperProcessRunner implementation — executes the real
/// helper process via Process.Start, captures stdout/stderr text, and
/// returns with the configured timeout.
///
/// On timeout or caller cancellation the entire process tree is killed
/// and the runner waits a bounded time for the process to exit. If the
/// process is still alive after that, a stable runner failure is returned
/// that names the still-running PID instead of pretending cleanup succeeded.
///
/// Caller responsible for process execution and for keeping this
/// implementation isolated from the main recording pipeline until the
/// WGC backend is ready.
/// </summary>
public sealed class WgcHelperProcessRunner : IWgcHelperProcessRunner
{
    private const int MaxOutputChars = 64 * 1024;
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReaderDrainTimeout = TimeSpan.FromSeconds(2);

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

        // Do not pass the caller's cancellation token to the reader tasks:
        // we want to drain whatever output was produced before/after the kill.
        var stdoutTask = ReadBoundedAsync(process.StandardOutput);
        var stderrTask = ReadBoundedAsync(process.StandardError);

        // Wait for process exit, timeout, or caller cancellation concurrently.
        // This avoids the previous synchronous WaitForExit(Timeout.Infinite)
        // pattern where cancellation could not reliably interrupt the wait.
        var exitTask = process.WaitForExitAsync();

        var cancelTcs = new TaskCompletionSource<object?>();
        using var registration = cancellationToken.Register(
            () => cancelTcs.TrySetResult(null),
            useSynchronizationContext: false);

        Task? timeoutTask = timeoutMs > 0 ? Task.Delay(timeoutMs) : null;

        Task completedTask = timeoutTask != null
            ? Task.WhenAny(exitTask, timeoutTask, cancelTcs.Task).GetAwaiter().GetResult()
            : Task.WhenAny(exitTask, cancelTcs.Task).GetAwaiter().GetResult();

        // "Confirmed normal exit" wins over a racing timeout or cancellation.
        // WaitForExitAsync may complete slightly after the process actually
        // exits, so check both the task state and the live process state.
        bool hasExited = false;
        try { hasExited = process.HasExited; } catch { }
        bool exitedNormally = exitTask.IsCompletedSuccessfully || hasExited;

        if (exitedNormally)
        {
            // Normal exit: give the readers a bounded window to finish after
            // the process exits.
            DrainReaders(stdoutTask, stderrTask);

            int exitCode;
            try { exitCode = process.ExitCode; }
            catch { exitCode = -1; }

            return new WgcHelperProcessResult
            {
                ExitCode = exitCode,
                StandardOutput = GetResultSafely(stdoutTask)?.Text ?? string.Empty,
                StandardError = GetResultSafely(stderrTask)?.Text ?? string.Empty,
                StandardOutputTruncated = GetResultSafely(stdoutTask)?.Truncated ?? false,
                StandardErrorTruncated = GetResultSafely(stderrTask)?.Truncated ?? false,
            };
        }

        // Timeout or cancellation path: kill the whole tree and wait bounded.
        bool timedOut = timeoutTask != null && ReferenceEquals(completedTask, timeoutTask);

        TryKillTree(process);
        bool exitedAfterKill = process.WaitForExit((int)KillWaitTimeout.TotalMilliseconds);

        // Drain the output readers with a bounded timeout so we do not leak
        // pipe handles or tasks, but still capture what the process emitted.
        DrainReaders(stdoutTask, stderrTask);

        if (!exitedAfterKill && !process.HasExited)
        {
            int pid = -1;
            try { pid = process.Id; } catch { }
            return new WgcHelperProcessResult
            {
                ExitCode = -1,
                StandardOutput = GetResultSafely(stdoutTask)?.Text ?? string.Empty,
                StandardError = (GetResultSafely(stderrTask)?.Text ?? string.Empty)
                    + $"\n.NET-side: WgcHelperProcessRunner {(timedOut ? "timed out" : "was cancelled")}; process did not exit after kill; pid={pid}",
                TimedOut = timedOut,
                Cancelled = !timedOut,
                StandardOutputTruncated = GetResultSafely(stdoutTask)?.Truncated ?? false,
                StandardErrorTruncated = GetResultSafely(stderrTask)?.Truncated ?? false,
            };
        }

        string stderrNote = timedOut
            ? "\n.NET-side: WgcHelperProcessRunner timed out; process was killed"
            : "\n.NET-side: WgcHelperProcessRunner was cancelled; process was killed";

        return new WgcHelperProcessResult
        {
            ExitCode = -1,
            StandardOutput = GetResultSafely(stdoutTask)?.Text ?? string.Empty,
            StandardError = (GetResultSafely(stderrTask)?.Text ?? string.Empty) + stderrNote,
            TimedOut = timedOut,
            Cancelled = !timedOut,
            StandardOutputTruncated = GetResultSafely(stdoutTask)?.Truncated ?? false,
            StandardErrorTruncated = GetResultSafely(stderrTask)?.Truncated ?? false,
        };
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
    }

    private sealed record BoundedReadResult(string Text, bool Truncated);

    private static async Task<BoundedReadResult> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder(Math.Min(MaxOutputChars, 4096));
        var buffer = new char[4096];
        bool truncated = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
                break;

            int remaining = MaxOutputChars - builder.Length;
            if (remaining > 0)
                builder.Append(buffer, 0, Math.Min(read, remaining));
            if (read > remaining)
                truncated = true;
        }

        return new BoundedReadResult(builder.ToString(), truncated);
    }

    private static void DrainReaders(Task<BoundedReadResult> stdoutTask, Task<BoundedReadResult> stderrTask)
    {
        try
        {
            Task.WhenAll(stdoutTask, stderrTask).Wait(ReaderDrainTimeout);
        }
        catch { /* best effort */ }
    }

    private static BoundedReadResult? GetResultSafely(Task<BoundedReadResult> task)
    {
        if (task == null) return null;
        if (task.IsCompletedSuccessfully) return task.Result;
        try
        {
            if (task.Wait(TimeSpan.FromMilliseconds(200))) return task.Result;
        }
        catch { /* fall through */ }
        return null;
    }
}
