using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;
using CliProgram = AgentRecorder.Cli.Program;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class CliQuickRecordingTests
{
    internal const string Input = """{"target":{"type":"selected_region"},"duration_seconds":10}""";
    private const string Id = "rec_123456abcdef";
    private const string Media = @"D:\owned\clip.mp4";
    private const string Secret = "synthetic-secret-never-echo";

    [Theory]
    [InlineData("""{"target":{"type":"selected_region"},"duration_seconds":31}""")]
    [InlineData("""{"target":{"type":"selected_region"},"stop_condition":{"type":"manual"}}""")]
    [InlineData("""{"target":{"type":"selected_region"},"duration_seconds":10,"mode":"screenshot_series"}""")]
    [InlineData("""{"target":{"type":"selected_region"},"duration_seconds":10,"nested":{}}""")]
    [InlineData("""{"target":{"type":"selected_region"},"duration_seconds":10,"duration_seconds":30}""")]
    [InlineData("""{"target":{"type":"future_window"},"duration_seconds":10}""")]
    [InlineData("""{"target":{"type":"selected_region"},"duration_seconds":10,"stop_condition":{"type":"duration","seconds":2}}""")]
    public async Task OutOfScopeNeverAcquiresHost(string input)
    {
        int starts = 0;
        var result = await CliProgram.QuickRecordAsync(1000, _ => Task.FromResult(input), _ =>
        {
            starts++;
            throw new InvalidOperationException();
        });
        Assert.Equal("not_submitted", result.Status);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task StartupFailureDoesNotTryAgainOrEchoException()
    {
        int attempts = 0;
        var result = await CliProgram.QuickRecordAsync(1000, _ => Task.FromResult(Input), _ =>
        {
            attempts++;
            throw new IOException(Secret);
        });
        Assert.Equal("host_unavailable", result.ReasonCode);
        Assert.Equal("not_submitted", result.Status);
        Assert.Equal(1, attempts);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task InputReadThatIgnoresCancellationCannotHoldTheCommandOrStartHost()
    {
        var input = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        try
        {
            var result = await CliProgram.QuickRecordAsync(1000, _ => input.Task, _ =>
            {
                starts++;
                throw new InvalidOperationException();
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("not_submitted", result.Status);
            Assert.Equal("deadline", result.ReasonCode);
            Assert.Equal(0, starts);
        }
        finally { input.TrySetResult(Input); }
    }

    [Theory]
    [InlineData("selection_cancelled")]
    [InlineData("selection_timeout")]
    public async Task SelectionOutcomeIsDefinitiveAndDoesNotRequestAnotherSelection(string outcome)
    {
        var wire = new Wire { Create = _ => Ok(new { status = outcome, quick = new { recording_created = false } }) };
        var result = await wire.Run();
        Assert.Equal("no_recording", result.Status);
        Assert.Equal(outcome, result.ReasonCode);
        Assert.Null(result.RecordingId);
        Assert.Equal(1, wire.Posts);
        Assert.Empty(wire.Reads);
    }

    [Fact]
    public async Task LocalApprovalExpiryPreservesIdAndDoesNotReadPublicationOrExitReusedHost()
    {
        var wire = new Wire { State = _ => Live("expired") };
        var result = await wire.Run();
        Assert.Equal("recording_failed", result.Status);
        Assert.Equal("approval_expired", result.ReasonCode);
        Assert.Equal(Id, result.RecordingId);
        Assert.Equal("reused", result.HostOwnership);
        Assert.Null(result.HostShutdown);
        Assert.Single(wire.Reads);
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData("recording")]
    [InlineData("pending_confirmation")]
    public async Task CreationResultWaitsForTerminalCommitIdentityAndOwnedIdleRetirement(string initial)
    {
        int polls = 0, history = 0, artifacts = 0, exits = 0;
        var wire = new Wire
        {
            Create = _ => Ok(new { status = initial, recording_id = Id, wait = new { timed_out = initial != "recording" } }),
            State = _ => Live(++polls == 1 ? "recording" : "completed"),
            History = _ => ++history <= 2 ? Reply(503, new { ok = false }) : Saved(),
            Artifact = _ => ++artifacts == 1 ? Checked(probe: "busy") : Checked(),
            Exit = _ => Task.FromResult(++exits == 1
                ? new CliProgram.ShutdownResult(false, "busy", "worker_retirement_unknown")
                : new CliProgram.ShutdownResult(true, "exited", "bound_process_exited"))
        };
        var result = await wire.Run();
        Assert.True(result.Ok);
        Assert.Equal(Media, result.OutputPath);
        Assert.Equal(73L, result.OutputSizeBytes);
        Assert.Equal(Id, result.RecordingId);
        Assert.Equal("owned", result.HostOwnership);
        Assert.Equal("exited", result.HostShutdown!.Status);
        Assert.Equal(2, polls);
        Assert.Equal(3, history);
        Assert.Equal(2, artifacts);
        Assert.Equal(2, exits);
        Assert.Equal(1, wire.Posts);
    }

    [Fact]
    public async Task SavedOutputSurvivesIdleExitFailure()
    {
        var wire = new Wire
        {
            Exit = _ => Task.FromResult(new CliProgram.ShutdownResult(false, "unknown", "accepted_exit_unconfirmed"))
        };
        var result = await wire.Run();
        Assert.True(result.Ok);
        Assert.Equal("completed", result.Status);
        Assert.Equal(Media, result.OutputPath);
        Assert.Equal("accepted_exit_unconfirmed", result.HostShutdown!.ReasonCode);
        Assert.Equal(1, wire.Posts);
    }

    [Theory]
    [InlineData("history_pending")]
    [InlineData("artifact_mismatch")]
    [InlineData("different_history")]
    [InlineData("disconnect")]
    public async Task IncompleteOrInterruptedEvidenceNeverReportsSuccess(string issue)
    {
        var wire = new Wire();
        if (issue == "history_pending") wire.History = _ => Reply(503, new { ok = false });
        if (issue == "artifact_mismatch") wire.Artifact = _ => Checked(identity: "mismatch");
        if (issue == "different_history") wire.History = _ => Saved(@"D:\owned\wrong.mp4");
        if (issue == "disconnect") wire.State = _ => throw new HttpRequestException(Secret);
        var result = await wire.Run(1000);
        Assert.False(result.Ok);
        Assert.Equal("outcome_unknown", result.Status);
        Assert.Equal(Id, result.RecordingId);
        Assert.Null(result.OutputPath);
        Assert.Equal(1, wire.Posts);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("server_error")]
    [InlineData("no_id")]
    public async Task AmbiguousPostIsUnknownAndNeverReplayed(string issue)
    {
        var wire = new Wire { Create = _ => issue switch
        {
            "server_error" => Reply(500, new { ok = false, error = new { message = Secret } }),
            "no_id" => Ok(new { status = "recording" }),
            _ => throw new HttpRequestException(Secret)
        } };
        var result = await wire.Run();
        Assert.Equal("submission_unknown", result.Status);
        Assert.Null(result.RecordingId);
        Assert.Equal(1, wire.Posts);
        Assert.Empty(wire.Reads);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsLoopbackChecksPeerAndIgnoresBadProxyWithoutFollowingRedirect(bool wrongPeer)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var first = await reader.ReadLineAsync(deadline.Token);
            if (first is null) return false;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
            var reply = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(reply, deadline.Token);
            return first == "GET /api/v1/capabilities HTTP/1.1";
        });
        var prior = HttpClient.DefaultProxy;
        try
        {
            HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:1") { BypassProxyOnLocal = false };
            using var client = CliProgram.CreateQuickClient(port, wrongPeer ? int.MaxValue : Environment.ProcessId, () => true);
            if (wrongPeer)
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("capabilities", deadline.Token));
            else
            {
                using var response = await client.GetAsync("capabilities", deadline.Token);
                Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            }
            Assert.Equal(!wrongPeer, await received.WaitAsync(deadline.Token));
        }
        finally { HttpClient.DefaultProxy = prior; }
    }

    [Fact]
    public async Task WindowsLoopbackPostWithLostReplyDoesNotOpenAnotherConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var reader = new StreamReader(peer.GetStream(), Encoding.ASCII);
            var first = await reader.ReadLineAsync(deadline.Token);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
            var body = new char[Input.Length];
            int count = await reader.ReadBlockAsync(body.AsMemory(), deadline.Token);
            return (first, new string(body, 0, count));
        });
        using var client = CliProgram.CreateQuickClient(port, Environment.ProcessId, () => true, singleConnection: true);
        using var body = new StringContent(Input, Encoding.ASCII, "application/json");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("recordings/quick", body, deadline.Token));
        Assert.Equal(("POST /api/v1/recordings/quick HTTP/1.1", Input), await received.WaitAsync(deadline.Token));
        Assert.False(listener.Pending());
    }

    private sealed class Wire
    {
        internal int Posts;
        internal readonly List<string> Reads = [];
        internal Func<CancellationToken, HttpResponseMessage> Create = _ => Ok(new { status = "recording", recording_id = Id });
        internal Func<CancellationToken, HttpResponseMessage> State = _ => Live("completed");
        internal Func<CancellationToken, HttpResponseMessage> History = _ => Saved();
        internal Func<CancellationToken, HttpResponseMessage> Artifact = _ => Checked();
        internal Func<int, Task<CliProgram.ShutdownResult>>? Exit;
        internal Task<CliProgram.QuickResult> Run(int timeout = 8000) =>
            CliProgram.QuickRecordAsync(timeout, _ => Task.FromResult(Input),
                _ => Task.FromResult(new CliProgram.QuickHost(Client(), Client(), "test-instance", Secret, Exit)));

        private HttpClient Client() => new(new Handler(this)) { BaseAddress = new Uri("http://127.0.0.1/api/v1/") };

        private sealed class Handler(Wire wire) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Assert.Equal(Secret, Assert.Single(request.Headers.GetValues("X-Agent-Recorder-Key")));
                Assert.Equal("test-instance", Assert.Single(request.Headers.GetValues("X-Agent-Recorder-Instance")));
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Post)
                {
                    wire.Posts++;
                    Assert.Equal("/api/v1/recordings/quick", path);
                    Assert.Equal(Input, await request.Content!.ReadAsStringAsync(token));
                    return wire.Create(token);
                }
                wire.Reads.Add(path);
                return path switch
                {
                    "/api/v1/recordings/" + Id => wire.State(token),
                    "/api/v1/recording-history/" + Id => wire.History(token),
                    "/api/v1/recording-history/" + Id + "/artifacts/check" => wire.Artifact(token),
                    _ => throw new InvalidOperationException()
                };
            }
        }
    }

    private static HttpResponseMessage Ok(object data) => Reply(200, new { ok = true, data });
    private static HttpResponseMessage Reply(int code, object body) =>
        new((HttpStatusCode)code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Live(string status) =>
        Ok(new { recording_id = Id, status, output = new { path = Media, bytes_written = 73 } });
    private static HttpResponseMessage Saved(string path = Media) =>
        Ok(new { recording = new { recording_id = Id, status = "completed", output_path = path, output_size_bytes = 73,
            completed_at = "2026-11-10T08:00:00Z" } });
    private static HttpResponseMessage Checked(string probe = "complete", string identity = "match") =>
        Ok(new { recording_id = Id, media_history_status = probe == "complete" ? "completed" : null, probe_state = probe, media = new
        {
            identity_state = identity, observation = "present_size_matches", recorded_size_bytes = 73, observed_size_bytes = 73
        } });
}
