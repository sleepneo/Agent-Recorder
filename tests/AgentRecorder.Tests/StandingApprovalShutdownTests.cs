using System.Diagnostics;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class StandingApprovalShutdownTests
{
    [Fact]
    public async Task RealCliStaysBusyAfterExpiryUntilNativeOwnerActuallyRetiresThenExitsBoundPrivateHost()
    {
        var executables = ShutdownTestExecutables.Current();
        var source = executables.OutputDirectory;
        var cli = executables.Cli;
        var root = Path.Combine(Path.GetTempPath(), "standing-shutdown-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(root, "private-bin"); Directory.CreateDirectory(files);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(files, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var app = Path.Combine(files, "AgentRecorder.Headless.exe"); File.Copy(executables.Host, app, overwrite: true);
        using var child = Process.Start(new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { root, "standing-shutdown" } })!;
        _ = child.Handle;
        async Task<JsonElement> Shutdown()
        {
            using var command = Process.Start(new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "shutdown", "--json", "--data-dir", root, "--timeout-ms", "10000" } })!;
            var output = await command.StandardOutput.ReadToEndAsync();
            await command.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            return JsonDocument.Parse(output).RootElement.Clone();
        }
        string? Status()
        {
            try
            {
                using var connection = new SqliteOperationalStore(Path.Combine(root, "fixture.db")).OpenReadOnlyConnection();
                using var read = connection.CreateCommand(); read.CommandText = "SELECT status_code FROM setup_intents LIMIT 1;";
                return read.ExecuteScalar() as string;
            }
            catch { return null; }
        }
        try
        {
            await StandingApprovalLifecycleTests.WaitUntil(() => File.Exists(Path.Combine(root, "visible")));
            var waiting = await Shutdown();
            Assert.Equal("busy", waiting.GetProperty("status").GetString());
            Assert.Equal("interaction_in_flight", waiting.GetProperty("reason_code").GetString());
            Assert.False(child.HasExited);
            await StandingApprovalLifecycleTests.WaitUntil(() => Status() == "expired");
            var uncertain = await Shutdown();
            Assert.Equal("busy", uncertain.GetProperty("status").GetString());
            Assert.False(File.Exists(Path.Combine(root, "idle")));
            Assert.False(child.HasExited);
            File.WriteAllText(Path.Combine(root, "release-preview"), "release");
            await StandingApprovalLifecycleTests.WaitUntil(() => File.Exists(Path.Combine(root, "idle")));
            using (var read = new SqliteOperationalStore(Path.Combine(root, "fixture.db")).OpenReadOnlyConnection())
            using (var cmd = read.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM recording_runs;"; Assert.Equal(0L, cmd.ExecuteScalar());
                cmd.CommandText = "SELECT COUNT(*) FROM consent_leases WHERE status_code='active';"; Assert.Equal(0L, cmd.ExecuteScalar());
                cmd.CommandText = "SELECT terminal_reason_code FROM setup_intents LIMIT 1;"; Assert.Equal("setup_intent_expired", cmd.ExecuteScalar());
            }
            var exited = await Shutdown(); Assert.Equal("exited", exited.GetProperty("status").GetString());
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, child.ExitCode);
            Assert.False(File.Exists(Path.Combine(root, "runtime", "ready.json")));
            Assert.False(File.Exists(Path.Combine(root, "must-not-exist.mp4")));
        }
        finally
        {
            File.WriteAllText(Path.Combine(root, "release-preview"), "release");
            if (!child.HasExited) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
            TestDirectoryCleanup.DeleteOwnedDirectory(root);
        }
    }
}
