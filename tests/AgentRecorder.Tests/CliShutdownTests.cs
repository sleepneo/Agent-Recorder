using System.Net;
using System.Text.Json;
using AgentRecorder.Cli;
using AgentRecorder.Infrastructure;
using Xunit;
using CliProgram = AgentRecorder.Cli.Program;

namespace AgentRecorder.Tests;

public sealed class CliShutdownTests
{
    [Theory]
    [InlineData(false, "not_running")]
    [InlineData(true, "unknown")]
    public async Task MissingReadyNeverStartsOrPosts(bool held, string status)
    {
        using var test = new Scenario();
        File.Delete(test.ReadyPath);
        var result = await test.Run(held);
        Assert.Equal(status, result.Status);
        Assert.Equal(0, test.Posts);
        Assert.Equal(0, test.Opens);
    }

    [Theory]
    [InlineData("pid_reused")]
    [InlineData("wrong_executable")]
    [InlineData("other_user")]
    [InlineData("wrong_data_dir")]
    [InlineData("stale_ready")]
    [InlineData("capabilities_mismatch")]
    [InlineData("nonce_changed")]
    public async Task IdentityFailureNeverPostsOrKills(string failure)
    {
        using var test = new Scenario();
        switch (failure)
        {
            case "pid_reused": test.Process.StartedAtUtcTicks++; break;
            case "wrong_executable": test.Process.ExecutablePath = @"C:\other.exe"; break;
            case "other_user": test.Process.ProcessUserSid = "other"; break;
            case "wrong_data_dir": test.Ready.DataDir += "-wrong"; test.Save(); break;
            case "stale_ready": test.MissingProcess = true; break;
            case "capabilities_mismatch": test.CapPid = 99; break;
            case "nonce_changed": test.ChangeNonceDuringCapabilities = true; break;
        }
        var result = await test.Run();
        Assert.False(result.Ok);
        Assert.Equal("unknown", result.Status);
        Assert.Equal(0, test.Posts);
    }

    [Theory]
    [InlineData("unsupported", "unsupported")]
    [InlineData("401", "unknown")]
    [InlineData("disconnect", "unknown")]
    [InlineData("busy", "busy")]
    [InlineData("identity_changed", "unknown")]
    [InlineData("timeout", "timeout")]
    [InlineData("exited", "exited")]
    public async Task OnlyBoundProcessExitAfterAcceptedMeansSuccess(string mode, string expected)
    {
        using var test = new Scenario { Mode = mode };
        var result = await test.Run();
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == "exited", result.Ok);
        Assert.Equal(mode == "unsupported" ? 0 : 1, test.Posts);
        if (mode is "timeout" or "exited") Assert.True(test.Process.Waited);
        else Assert.False(test.Process.Waited);
        Assert.True(File.Exists(test.ReadyPath)); // CLI never deletes ready, including stale/exit.
    }

    [Fact]
    public async Task DisconnectedPortAndAlreadyExitedDoNotProveGracefulExit()
    {
        using var test = new Scenario();
        test.Process.HasExited = true;
        var result = await test.Run();
        Assert.Equal("unknown", result.Status);
        Assert.Equal(0, test.Posts);
    }

    private sealed class BoundProcess : CliProgram.IShutdownProcess
    {
        public long StartedAtUtcTicks { get; set; } = 123456789;
        public string ExecutablePath { get; set; } = @"C:\private\AgentRecorder.App.exe";
        public string ProcessUserSid { get; set; } = "test-sid";
        public bool HasExited { get; set; }
        internal bool Waited, ExitOnWait = true;
        public async Task WaitForExitAsync(CancellationToken cancellation)
        {
            Waited = true;
            if (!ExitOnWait) await Task.Delay(Timeout.Infinite, cancellation);
            HasExited = true;
        }
        public void Dispose() { }
    }
    private sealed class Scenario : HttpMessageHandler
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "task365-cli-" + Guid.NewGuid().ToString("N"));
        internal string ReadyPath => Path.Combine(Root, "runtime", "ready.json");
        internal ReadySnapshot Ready;
        internal BoundProcess Process = new();
        internal int Posts, Opens;
        internal int? CapPid;
        internal bool MissingProcess, ChangeNonceDuringCapabilities;
        internal string Mode = "exited";
        internal Scenario()
        {
            Directory.CreateDirectory(Path.Combine(Root, "runtime"));
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            File.WriteAllText(Path.Combine(Root, "config", "api-key.txt"), "private-test-key");
            Ready = new ReadySnapshot { Ready = true, Pid = 7, Port = 12345, Mode = "headless", DataDir = Root,
                ReadyFile = ReadyPath, ApiKeyFile = Path.Combine(Root, "config", "api-key.txt"),
                InstanceId = Guid.NewGuid().ToString("D"), ProcessStartedAtUtcTicks = Process.StartedAtUtcTicks,
                ExecutablePath = Process.ExecutablePath, ProcessUserSid = "test-sid" };
            Save();
        }
        internal void Save() => File.WriteAllText(ReadyPath, JsonSerializer.Serialize(Ready, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        internal async Task<CliProgram.ShutdownResult> Run(bool held = false)
        {
            using var http = new HttpClient(this, disposeHandler: false);
            Process.ExitOnWait = Mode != "timeout";
            return await CliProgram.ShutdownCoreAsync(Root, 300, http, _ => { Opens++; return MissingProcess ? null : Process; }, "test-sid", () => held);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            HttpResponseMessage Reply(int status, object value) => new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })) };
            if (request.Method == HttpMethod.Get)
            {
                var copy = JsonSerializer.Deserialize<ReadySnapshot>(JsonSerializer.Serialize(Ready))!;
                copy.Pid = CapPid ?? Ready.Pid;
                var response = Reply(200, new { ok = true, data = new { readiness = copy,
                    host_shutdown = new { supported = Mode != "unsupported", method = "POST", idle_only = true, force = false, endpoint = "/api/v1/host/shutdown" } } });
                if (ChangeNonceDuringCapabilities) { Ready.InstanceId = Guid.NewGuid().ToString("D"); Save(); }
                return Task.FromResult(response);
            }
            Posts++;
            Assert.Equal("private-test-key", request.Headers.GetValues("X-Agent-Recorder-Key").Single());
            Assert.Equal(Ready.InstanceId, request.Headers.GetValues("X-Agent-Recorder-Instance").Single());
            return Mode switch
            {
                "disconnect" => throw new HttpRequestException("private transport dropped"),
                "401" => Task.FromResult(Reply(401, new { ok = false })),
                "busy" => Task.FromResult(Reply(409, new { ok = false, error = new { code = "HOST_BUSY", details = new { reason_code = "recording_recording" } } })),
                "identity_changed" => Task.FromResult(Reply(409, new { ok = false, error = new { code = "HOST_IDENTITY_MISMATCH", details = new { reason_code = "host_identity_mismatch" } } })),
                _ => Task.FromResult(Reply(202, new { ok = true, data = new { status = "accepted", exited = false } }))
            };
        }
    }
}
