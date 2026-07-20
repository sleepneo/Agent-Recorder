using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentRecorder.Capture;

/// <summary>
/// Production implementation of <see cref="IExternalProcessRunner"/>.
/// Uses <see cref="ProcessStartInfo.ArgumentList"/>, captures a limited
/// stderr excerpt, kills the process tree on timeout or caller cancellation,
/// and never leaks unobserved exceptions.
/// </summary>
public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    private const int MaxStderrChars = 4000;
    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StderrDrainTimeout = TimeSpan.FromSeconds(2);

    public async Task<ExternalProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> argumentList,
        TimeSpan timeout,
        bool captureStderr = true,
        Encoding? stderrEncoding = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Executable path cannot be empty.", nameof(fileName));

        var stderr = new StringBuilder();
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false,
                RedirectStandardError = captureStderr,
                RedirectStandardOutput = false,
                RedirectStandardInput = false,
                StandardErrorEncoding = stderrEncoding ?? Encoding.Default
            },
            EnableRaisingEvents = true
        };

        foreach (var arg in argumentList ?? Array.Empty<string>())
            proc.StartInfo.ArgumentList.Add(arg);

        var stderrEof = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (captureStderr)
        {
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    stderrEof.TrySetResult(true);
                    return;
                }
                lock (stderr)
                {
                    if (stderr.Length < MaxStderrChars)
                    {
                        stderr.AppendLine(e.Data);
                        if (stderr.Length > MaxStderrChars)
                            stderr.Length = MaxStderrChars;
                    }
                }
            };
        }

        bool timedOut = false;
        try
        {
            proc.Start();
            if (captureStderr)
                proc.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            try
            {
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await KillTreeAndWaitAsync(proc);
                    throw;
                }

                timedOut = true;
                await KillTreeAndWaitAsync(proc);
            }

            // The process has exited, but the asynchronous stderr reader may not
            // have delivered its EOF yet. Wait for the real EOF (e.Data == null)
            // so trailing stderr is preserved, but only when we are capturing.
            if (captureStderr && !stderrEof.Task.IsCompleted)
            {
                using var drainCts = new CancellationTokenSource(StderrDrainTimeout);
                try { await stderrEof.Task.WaitAsync(drainCts.Token).ConfigureAwait(false); }
                catch { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await KillTreeAndWaitAsync(proc);
            throw;
        }

        int exitCode;
        try { exitCode = proc.HasExited ? proc.ExitCode : -1; } catch { exitCode = -1; }
        string stderrSnapshot;
        lock (stderr) stderrSnapshot = stderr.ToString();
        return new ExternalProcessResult(exitCode, timedOut, stderrSnapshot);
    }

    /// <summary>
    /// Kills the whole process tree and waits for it to exit with a bounded
    /// timeout. Does not use the caller's cancellation token so that cleanup
    /// is not skipped when the caller has already cancelled.
    /// </summary>
    private static async Task KillTreeAndWaitAsync(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(true);
        }
        catch { }

        using var cleanupCts = new CancellationTokenSource(KillWaitTimeout);
        try
        {
            await proc.WaitForExitAsync(cleanupCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // We waited the full bounded timeout; the process may still be
            // exiting, but we have done our best-effort cleanup.
        }
        catch
        {
            // Ignore races (process already disposed, handle closed, etc.).
        }
    }
}
