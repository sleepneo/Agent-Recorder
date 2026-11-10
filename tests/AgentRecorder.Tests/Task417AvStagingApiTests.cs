using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class ManagedVideoVolumeCapacity407Tests
{
    private const string StagingUnavailable = "RETENTION_STAGING_UNAVAILABLE";

    [Theory]
    [InlineData("tray", "App")]
    [InlineData("headless", "Headless")]
    public async Task Task417RealChildObservesOnlyFrozenStagingAndReturnsAggregate(string mode, string program)
    {
        using var host = new Host(mode, _ => RealProvider(program), staging: true);
        var root = Path.Combine(host.Root, "temp");
        var capability = await host.Capability();
        Assert.True(capability.GetProperty("supported").GetBoolean());
        Assert.Equal("/api/v1/retention/staging", capability.GetProperty("endpoint").GetString());
        Assert.Equal("av_split_staging", capability.GetProperty("scope").GetString());
        Assert.True(capability.GetProperty("read_only").GetBoolean());
        Assert.Equal(512, capability.GetProperty("entry_limit").GetInt32());
        Assert.Equal(2000, capability.GetProperty("sample_timeout_ms").GetInt32());
        Assert.Equal(1, capability.GetProperty("actual_workers_max").GetInt32());
        Assert.Null(host.Provider.LastProbe);
        Assert.False(Directory.Exists(root));

        var empty = await host.Send();
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.True(empty.Json.GetProperty("data").GetProperty("complete").GetBoolean());
        Assert.Equal(0, empty.Json.GetProperty("data").GetProperty("logical_bytes").GetInt64());
        Assert.False(Directory.Exists(root));

        var candidate = Directory.CreateDirectory(Path.Combine(root, ".agent-recorder-av-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllBytes(Path.Combine(candidate, "private-title_video.mp4"), new byte[37]);
        File.WriteAllBytes(Path.Combine(candidate, "private-title_audio.wav"), new byte[13]);
        File.WriteAllBytes(Path.Combine(candidate, "diagnostics.json"), new byte[61]);
        File.WriteAllBytes(Path.Combine(root, ".agent-recorder-av-" + Guid.NewGuid().ToString("N")), new byte[51]);
        var before = Snapshot(host.Root);
        var started = DateTimeOffset.UtcNow;
        var observed = await host.Send();
        Assert.Equal(HttpStatusCode.OK, observed.Status);
        var sample = observed.Json.GetProperty("data");
        Assert.Equal(1, sample.GetProperty("observed_directories").GetInt32());
        Assert.Equal(2, sample.GetProperty("verified_files").GetInt32());
        Assert.Equal(50, sample.GetProperty("logical_bytes").GetInt64());
        Assert.Equal(1, sample.GetProperty("skipped_entries").GetInt32());
        Assert.False(sample.GetProperty("complete").GetBoolean());
        Assert.Equal("entry_unavailable", Assert.Single(sample.GetProperty("reason_codes").EnumerateArray()).GetString());
        Assert.InRange(sample.GetProperty("sampled_at_utc").GetDateTimeOffset(), started, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("private-title", observed.Json.GetRawText());
        Assert.DoesNotContain(".agent-recorder-av-", observed.Json.GetRawText());
        Assert.DoesNotContain(host.Root.Replace("\\", "\\\\"), observed.Json.GetRawText());
        Assert.Equal(before, Snapshot(host.Root));
        Assert.Equal(0, host.BackendStarts);
        Assert.True(host.Provider.LastProbe!.RetirementCompletion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Task417ApiRejectsUntrustedRequestsBeforeLaunchingAChild()
    {
        using var host = new Host(staging: true);
        AssertFailure(await host.Send(auth: false), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        AssertFailure(await host.Send(key: "wrong"), HttpStatusCode.Forbidden, "FORBIDDEN");
        AssertFailure(await host.Send(HttpMethod.Post), HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
        foreach (var query in new[] { "?path=C%3A%5C", "?filter=*", "?recursive=true", "?limit=1", "?" })
            AssertFailure(await host.Send(suffix: query), HttpStatusCode.BadRequest, "INVALID_ARGUMENT");
        AssertFailure(await host.Send(body: "{}"), HttpStatusCode.BadRequest, "INVALID_ARGUMENT");
        foreach (var headers in new[] { "Content-Length: -0\r\n", "Content-Length: 0\r\nContent-Length: 0\r\n",
            "Transfer-Encoding: chunked\r\n", "Content-Length: 2147483647\r\n" })
            Assert.Contains("HTTP/1.1 400", await host.Raw(headers));
        ApiKeyAuth.ResetForTesting(host.Root);
        AssertFailure(await host.Send(auth: false), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Null(host.Provider.LastProbe);
        Assert.False(Directory.Exists(Path.Combine(host.Root, "temp")));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Task417UnreadyHostCannotLaunch(bool configured, bool ready, bool interactive)
    {
        using var host = new Host("tray", configured: configured, ready: ready, interactive: interactive, staging: true);
        Assert.False((await host.Capability()).GetProperty("supported").GetBoolean());
        AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, StagingUnavailable, "not_ready");
        Assert.Null(host.Provider.LastProbe);
    }

    [Fact]
    public async Task Task417UnverifiableRootDoesNotReturnZeroAndCanBeRetried()
    {
        using var host = new Host(staging: true);
        var root = Path.Combine(host.Root, "temp");
        File.WriteAllText(root, "owned file in place of directory");
        var before = Snapshot(host.Root);
        var failed = await host.Send();
        AssertFailure(failed, HttpStatusCode.ServiceUnavailable, StagingUnavailable, "root_unavailable");
        Assert.False(failed.Json.TryGetProperty("data", out _));
        Assert.Equal(before, Snapshot(host.Root));
        File.Delete(root);
        Assert.Equal(HttpStatusCode.OK, (await host.Send()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Task417TimeoutAndSocketResetCannotAdmitAnotherChildBeforeExactRetirement(bool reset)
    {
        var entered = Signal();
        var releaseRead = Signal();
        var killEntered = Signal();
        using var releaseKill = new ManualResetEventSlim();
        int launches = 0;
        using var host = new Host(provider: _ => RealProvider("Headless", new StorageProbeHooks
        {
            BeforeRead = () =>
            {
                if (Interlocked.Increment(ref launches) != 1) return Task.CompletedTask;
                entered.TrySetResult();
                return releaseRead.Task;
            },
            Kill = process =>
            {
                killEntered.TrySetResult();
                if (!releaseKill.Wait(Bound)) throw new TimeoutException("Owned fixture kill gate.");
                if (!process.HasExited) process.Kill();
            }
        }), staging: true);

        var elapsed = Stopwatch.StartNew();
        using var client = await host.OpenRaw();
        try
        {
            await entered.Task.WaitAsync(Bound);
            var lifetime = host.Provider.LastProbe!;
            if (reset)
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Client.Close(0);
                await killEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(1500));
            }
            else
            {
                using var response = new StreamReader(client.GetStream());
                var raw = await response.ReadToEndAsync().WaitAsync(Bound);
                Assert.Contains("HTTP/1.1 503", raw);
                Assert.Contains("\"reason_code\":\"timeout\"", raw);
                Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(1800), TimeSpan.FromSeconds(5));
                await killEntered.Task.WaitAsync(Bound);
            }
            Assert.False(lifetime.ExitCompletion.IsCompleted);
            Assert.False(lifetime.Process.HasExited);
            Assert.False(lifetime.RetirementCompletion.IsCompleted);
            for (int i = 0; i < 3; i++)
                AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, StagingUnavailable, "probe_busy");
            AssertFailure(await host.Shutdown(), HttpStatusCode.Conflict, "HOST_BUSY", "staging_inventory_pending");
            Assert.Same(lifetime, host.Provider.LastProbe);
            Assert.Equal(1, launches);
            releaseKill.Set();
            releaseRead.TrySetResult();
            Assert.True(await host.Staging!.WaitForIdleAsync().WaitAsync(Bound));
            Assert.True(lifetime.RetirementCompletion.IsCompletedSuccessfully);
            Assert.Equal(HttpStatusCode.OK, (await host.Send()).Status);
            Assert.Equal(2, launches);
        }
        finally { releaseKill.Set(); releaseRead.TrySetResult(); }
    }

    [Fact]
    public async Task Task417UncertainRetirementKeepsAdmissionAndIdleShutdownClosed()
    {
        int launches = 0;
        using var host = new Host(provider: _ => RealProvider("Headless", new StorageProbeHooks
        {
            Started = _ => Interlocked.Increment(ref launches),
            BeforeDispose = () => throw new IOException("Private staging cleanup detail")
        }), staging: true);
        var cancelled = await Assert.ThrowsAsync<ApiException>(() => host.Staging!.ReadAsync(new CancellationToken(true)));
        Assert.Equal(StagingUnavailable, cancelled.Code);
        Assert.Null(host.Provider.LastProbe);

        var failed = await host.Send();
        AssertFailure(failed, HttpStatusCode.ServiceUnavailable, StagingUnavailable, "probe_retirement_unknown");
        Assert.DoesNotContain("Private staging cleanup detail", failed.Json.GetRawText());
        Assert.False(await host.Staging!.WaitForIdleAsync());
        AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, StagingUnavailable, "probe_retirement_unknown");
        AssertFailure(await host.Shutdown(), HttpStatusCode.Conflict, "HOST_BUSY", "staging_inventory_retirement_unknown");
        Assert.Equal(1, launches);
        Assert.False(host.Server.Stop(Bound));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Sample\":{},\"FailureReason\":null}")]
    [InlineData("{\"Sample\":null,\"FailureReason\":null}")]
    [InlineData("{\"Sample\":null,\"FailureReason\":\"C:\\\\private\"}")]
    [InlineData("{\"Sample\":null,\"FailureReason\":\"root_unavailable\",\"FailureReason\":\"root_changed\"}")]
    public void Task417MalformedPrivateResponseCannotImplyEmptyInventory(string response)
    {
        var error = Record.Exception(() => WindowsAvStagingInventory.ReadResponse(response));
        Assert.True(error is JsonException or IOException);
    }
}
