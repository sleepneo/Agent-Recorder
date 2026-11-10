using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class TempRetentionPolicyStartupTests(ITestOutputHelper output)
{
    private const string Temp = "temp";
    private const string Failed = "failed";
    private const string OldFailed = "failed/unregistered-old";
    private const string RecentFailed = "failed/unregistered-recent";
    private static readonly string[] Directories = [Temp, Failed, OldFailed, RecentFailed];
    private static readonly string[] Files =
    [
        "temp/old_video.mp4", "temp/old_audio.wav", "temp/recent_video.mp4",
        "temp/control.txt", "outside_video.mp4", OldFailed + "/diagnostic.txt", RecentFailed + "/diagnostic.txt"
    ];

    [Theory]
    [InlineData("App", true)]
    [InlineData("Headless", true)]
    [InlineData("App", false)]
    [InlineData("Headless", false)]
    public async Task StartupAndGracefulExitPreserveExistingStagingAndDiagnostics(string program, bool withMarkers)
    {
        using var data = new TempDirectory();
        var root = data.Path;
        if (withMarkers) SeedMarkers(root);
        var before = Snapshot(root);
        var headless = program == "Headless";
        var executable = Executable("src", program);
        var readyPath = Path.Combine(root, "runtime", "ready.json");
        var shutdownName = @"Local\AgentRecorder.Task410." + Guid.NewGuid().ToString("N");
        using var shutdown = headless ? new EventWaitHandle(false, EventResetMode.AutoReset, shutdownName) : null;
        Assert.DoesNotContain(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), address => address.Port == ApiServer.Port);
        Assert.True(File.Exists(executable), $"Missing built host {executable}");
        var start = Hidden(executable);
        start.Environment["AGENT_RECORDER_DATA_DIR"] = root;
        if (headless)
        {
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(root);
            start.ArgumentList.Add("--shutdown-event-name");
            start.ArgumentList.Add(shutdownName);
        }

        using var host = Process.Start(start)!;
        _ = host.Handle;
        ReadySnapshot? ready = null;
        var gracefulRequested = false;
        string atReady;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!File.Exists(readyPath) && !host.HasExited)
                await Task.Delay(25, deadline.Token);
            Assert.False(host.HasExited, $"{program} exited before readiness.");
            var snapshot = JsonSerializer.Deserialize<ReadySnapshot>(await File.ReadAllTextAsync(readyPath, deadline.Token), ApiResponse.Json)!;
            Assert.True(snapshot.Ready);
            Assert.Equal(host.Id, snapshot.Pid);
            Assert.Equal(headless ? "headless" : "tray", snapshot.Mode);
            Assert.Equal(root, snapshot.DataDir, ignoreCase: true);
            Assert.Equal(executable, snapshot.ExecutablePath, ignoreCase: true);
            Assert.Equal(ApiServer.Port, snapshot.Port);
            ready = snapshot;
            atReady = Snapshot(root);
        }
        finally
        {
            try
            {
                if (!host.HasExited)
                {
                    if (shutdown is not null)
                        gracefulRequested = shutdown.Set();
                    else if (ready is not null)
                    {
                        await ShutdownApp(root, host.Id);
                        gracefulRequested = true;
                    }
                    await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                }
            }
            finally
            {
                // Failure cleanup can only terminate the exact process started by this fixture.
                if (!host.HasExited)
                {
                    host.Kill();
                    await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }

        var afterExit = Snapshot(root);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            program, withMarkers, ready.Pid, ready.Mode, ready.Port,
            gracefulRequested,
            exitCode = host.ExitCode,
            readyRemoved = !File.Exists(readyPath),
            before = JsonSerializer.Deserialize<JsonElement>(before),
            atReady = JsonSerializer.Deserialize<JsonElement>(atReady),
            afterExit = JsonSerializer.Deserialize<JsonElement>(afterExit)
        }));
        Assert.True(gracefulRequested);
        Assert.Equal(0, host.ExitCode);
        Assert.False(File.Exists(readyPath));
        Assert.Equal(before, atReady);
        Assert.Equal(before, afterExit);
    }

    private static void SeedMarkers(string root)
    {
        var old = DateTime.UtcNow.AddHours(-25);
        foreach (var directory in Directories)
            Directory.CreateDirectory(Path.Combine(root, directory));
        foreach (var relative in Files)
        {
            var path = Path.Combine(root, relative);
            File.WriteAllText(path, "synthetic marker: " + relative);
            if (!relative.Contains("recent", StringComparison.Ordinal))
                File.SetLastWriteTimeUtc(path, old);
        }
        Directory.SetLastWriteTimeUtc(Path.Combine(root, OldFailed), old);
    }

    private static string Snapshot(string root)
    {
        var directories = Directories.Select(relative =>
        {
            var path = Path.Combine(root, relative);
            return new { path = relative, lastWriteUtc = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : (DateTime?)null };
        });
        var files = Files.Select(relative =>
        {
            var path = Path.Combine(root, relative);
            return File.Exists(path)
                ? new { path = relative, lastWriteUtc = (DateTime?)File.GetLastWriteTimeUtc(path), sha256 = (string?)Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }
                : new { path = relative, lastWriteUtc = (DateTime?)null, sha256 = (string?)null };
        });
        return JsonSerializer.Serialize(new { directories, files });
    }

    private static string Executable(string projectArea, string program)
    {
        var root = TestHelper.ProjectRoot;
        var build = Path.GetRelativePath(Path.Combine(root, "tests", "AgentRecorder.Tests"), AppContext.BaseDirectory);
        return Path.Combine(root, projectArea, "AgentRecorder." + program, build, $"AgentRecorder.{program}.exe");
    }

    private static ProcessStartInfo Hidden(string executable) => new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = Path.GetDirectoryName(executable)!
    };

    private static async Task ShutdownApp(string root, int pid)
    {
        var start = Hidden(Executable("tools", "Cli"));
        start.RedirectStandardOutput = true;
        foreach (var argument in new[] { "shutdown", "--json", "--data-dir", root, "--timeout-ms", "15000" })
            start.ArgumentList.Add(argument);
        using var cli = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stdout = await cli.StandardOutput.ReadToEndAsync(deadline.Token);
        await cli.WaitForExitAsync(deadline.Token);
        Assert.Equal(0, cli.ExitCode);
        using var response = JsonDocument.Parse(stdout);
        Assert.Equal("exited", response.RootElement.GetProperty("status").GetString());
        Assert.Equal("bound_process_exited", response.RootElement.GetProperty("reason_code").GetString());
        Assert.Equal(pid, response.RootElement.GetProperty("pid").GetInt32());
    }
}
