using System.Diagnostics;
using System.Text.Json;
using AgentRecorder.Cli;
using AgentRecorder.Infrastructure;
using Xunit;
using CliProgram = AgentRecorder.Cli.Program;

namespace AgentRecorder.Tests;

public sealed class PrivateHostShutdownTests
{
    [Fact]
    public async Task RealCliWaitsForPrivateProcessReadyAndMutexRetirementThenEnsureDecisionAllowsRestart()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AgentRecorder.sln"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var outputFlavor = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Name;
        var root = Path.Combine(repo.FullName, ".local-data", "temp", outputFlavor.ToLowerInvariant(), "child-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(root, "private-bin");
        Directory.CreateDirectory(files);
        var source = Path.Combine(repo.FullName, "tests", "AgentRecorder.FutureWindowUiTestHost", "bin", outputFlavor, "Release", "net8.0-windows10.0.19041.0");
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(files, Path.GetFileName(file)), true);
        var app = Path.Combine(files, "AgentRecorder.Headless.exe");
        File.Copy(Path.Combine(source, "AgentRecorder.FutureWindowUiTestHost.exe"), app, true);
        var readyPath = Path.Combine(root, "runtime", "ready.json");
        var cli = Path.Combine(repo.FullName, "tools", "AgentRecorder.Cli", "bin", outputFlavor, "Release", "net8.0-windows10.0.19041.0", "AgentRecorder.Cli.exe");
        string? previousNonce = null;
        for (int run = 0; run < 2; run++)
        {
            var decision = CliProgram.EvaluateStaleReadyDecision(readyPath, new CliProgram.StaleReadyDecisionContext
            {
                // Use the private fixture's mutex, never query/change the user's running host.
                IsMutexHeld = () => false
            });
            Assert.Equal(CliProgram.StaleReadyDecisionAction.ProceedToStart, decision.Action);
            var ensured = await Task.Run(() => CliProgram.EnsureRunningCore(
                new CliOptions { PreferHeadless = true, AppPath = app, DataDir = root, TimeoutMs = 10000 },
                InteractiveDesktopRuntime.ObserveCurrent(), new CliProgram.StaleReadyDecisionContext { IsMutexHeld = () => false }));
            Assert.True(ensured.Ok, ensured.Code + ": " + ensured.Message);
            Assert.Equal("started", ensured.Source);
            using var child = Process.GetProcessById(ensured.Pid);
            _ = child.Handle; // Retain the original OS process before shutdown, including exit code.
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!File.Exists(readyPath) && !child.HasExited) await Task.Delay(20, deadline.Token);
                Assert.False(child.HasExited, "Private host failed before readiness.");
                var ready = JsonSerializer.Deserialize<ReadySnapshot>(await File.ReadAllTextAsync(readyPath), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
                Assert.NotEqual(previousNonce, ready.InstanceId);
                previousNonce = ready.InstanceId;
                Assert.Equal(child.Id, ready.Pid);
                var invoke = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "shutdown", "--json", "--data-dir", root, "--timeout-ms", "10000" }) invoke.ArgumentList.Add(arg);
                using var command = Process.Start(invoke)!;
                var stdout = await command.StandardOutput.ReadToEndAsync();
                await command.WaitForExitAsync(deadline.Token);
                await child.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, child.ExitCode);
                Assert.Equal(0, command.ExitCode);
                Assert.Equal("exited", JsonDocument.Parse(stdout).RootElement.GetProperty("status").GetString());
                Assert.False(File.Exists(readyPath));
                using var released = SingleInstanceGuard.TryAcquire(@"Local\AgentRecorder.Task365." + Path.GetFileName(root));
                Assert.True(released.IsAcquired);
            }
            finally
            {
                // Fixture has its own finite natural lifetime; no Kill, tray, or global mutex operations.
                if (!child.HasExited) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            }
        }
    }
}
