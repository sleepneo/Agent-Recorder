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

    public async Task<ExternalProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> argumentList,
        TimeSpan timeout,
        bool captureStderr = true,
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
                RedirectStandardInput = false
            },
            EnableRaisingEvents = true
        };

        foreach (var arg in argumentList ?? Array.Empty<string>())
            proc.StartInfo.ArgumentList.Add(arg);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => tcs.TrySetResult(true);

        if (captureStderr)
        {
            proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
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

        using (cancellationToken.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false))
        {
            proc.Start();
            if (captureStderr)
                proc.BeginErrorReadLine();

            bool timedOut = false;
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                using (cts.Token.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false))
                {
                    try { await tcs.Task; }
                    catch (OperationCanceledException)
                    {
                        if (cancellationToken.IsCancellationRequested) throw;
                    }
                }

                timedOut = !proc.HasExited;
                if (timedOut)
                {
                    await KillTreeAndWaitAsync(proc);
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
