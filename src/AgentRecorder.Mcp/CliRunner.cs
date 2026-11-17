using System.Diagnostics;
using System.Text;

namespace AgentRecorder.Mcp;

internal sealed record CliOutcome(string ReasonCode, int? ExitCode = null, string? Output = null);

internal static class CliRunner
{
    public const int OutputLimit = 65536;
    public const int CliTimeoutMs = 10000;
    public const int TotalTimeoutMs = 15000;

    public static async Task<CliOutcome> RunAsync(Binding binding, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TotalTimeoutMs);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var start = new ProcessStartInfo(binding.CliPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(binding.CliPath)!
        };
        foreach (var argument in new[] { "ensure-running", "--json", "--package-root", binding.PackageRoot,
            "--app", binding.AppPath, "--data-dir", binding.DataDir, "--tray", "--timeout-ms", CliTimeoutMs.ToString() })
            start.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        var process = new Process { StartInfo = start };
        try { if (!process.Start()) { process.Dispose(); return new("cli_not_started"); } }
        catch { process.Dispose(); return new("cli_not_started"); }

        // No Job, Kill, shell, Stop/Shutdown or process-tree cleanup. Timeout/cancellation
        // ends observation only. Continue draining bounded buffers until natural CLI exit.
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = DrainAsync(process.StandardOutput, overflow);
        var stderr = DrainAsync(process.StandardError, overflow);
        var retirement = RetireAsync(process, stdout, stderr);
        try { process.StandardInput.Close(); } // CLI receives EOF, never the MCP protocol input.
        catch { return new("cli_observation_unknown"); }
        try
        {
            var winner = await Task.WhenAny(retirement, overflow.Task).WaitAsync(stop.Token);
            if (winner == overflow.Task || overflow.Task.IsCompleted) return new("cli_output_limit_unknown");
            return await retirement;
        }
        catch (OperationCanceledException)
        { return new(cancellationToken.IsCancellationRequested ? "cli_cancelled_unknown" : "cli_timeout_unknown"); }
    }

    private static async Task<string> DrainAsync(StreamReader reader, TaskCompletionSource overflow)
    {
        var buffer = new char[2048];
        var collected = new StringBuilder();
        long count = 0;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer)) != 0)
            {
                count += read;
                if (count > OutputLimit) overflow.TrySetResult();
                if (collected.Length < OutputLimit)
                    collected.Append(buffer, 0, Math.Min(read, OutputLimit - collected.Length));
            }
        }
        catch (IOException) { overflow.TrySetResult(); }
        return collected.ToString();
    }

    private static async Task<CliOutcome> RetireAsync(Process process, Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await process.WaitForExitAsync();
            var output = await stdout;
            await stderr; // Drain but never publish logs, exception bodies or credentials.
            return new("cli_exited", process.ExitCode, output);
        }
        catch { return new("cli_observation_unknown"); }
        finally { process.Dispose(); }
    }
}
