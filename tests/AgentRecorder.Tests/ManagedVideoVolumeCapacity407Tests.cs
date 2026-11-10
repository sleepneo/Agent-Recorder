using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed partial class ManagedVideoVolumeCapacity407Tests
{
    private const string Endpoint = "/api/v1/retention/storage";
    private const string Volume = @"\\?\Volume{00112233-4455-6677-8899-aabbccddeeff}\";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(7);

    [Theory]
    [InlineData("tray", "App", true)]
    [InlineData("headless", "Headless", false)]
    public async Task ReadyHostUsesItsRealProbeForTheManagedRoot(string mode, string programName, bool createVideos)
    {
        var program = programName == "App" ? typeof(AgentRecorder.App.Program) : typeof(AgentRecorder.Headless.Program);
        var readCalls = typeof(RetentionRuntimeHostTests).GetMethod("Calls", BindingFlags.Static | BindingFlags.NonPublic)!;
        var run = program.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!;
        var calls = (IEnumerable<MethodBase>)readCalls.Invoke(null, new object[] { run })!;
        Assert.Contains(typeof(ApiServer).GetMethod("ConfigureRetentionStorage", BindingFlags.Instance | BindingFlags.NonPublic)!, calls);

        using var host = new Host(mode, root => RealProvider(programName));
        var recordingProvider = RecordingPreflightChecker.StorageCapacityProvider;
        if (createVideos)
        {
            Directory.CreateDirectory(host.Videos);
            File.WriteAllText(Path.Combine(host.Videos, "owned-marker.txt"), "owned test data");
        }
        var before = Snapshot(host.Root);
        var capability = await host.Capability();
        Assert.True(capability.GetProperty("supported").GetBoolean());
        Assert.Equal(Endpoint, capability.GetProperty("endpoint").GetString());
        Assert.Equal("managed_videos_volume", capability.GetProperty("scope").GetString());
        Assert.True(capability.GetProperty("read_only").GetBoolean());
        Assert.Equal(2000, capability.GetProperty("sample_timeout_ms").GetInt32());
        Assert.Equal(1, capability.GetProperty("actual_workers_max").GetInt32());
        Assert.Null(host.Provider.LastProbe);

        var beforeSample = DateTimeOffset.UtcNow;
        var (status, json) = await host.Send();
        Assert.Equal(HttpStatusCode.OK, status);
        var data = json.GetProperty("data");
        Assert.Equal("managed_videos_volume", data.GetProperty("scope").GetString());
        Assert.True(data.GetProperty("available_bytes").GetInt64() >= 0);
        var sampled = data.GetProperty("sampled_at_utc").GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, sampled.Offset);
        Assert.InRange(sampled, beforeSample, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "available_bytes", "sampled_at_utc", "scope" },
            data.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(createVideos, Directory.Exists(host.Videos));
        Assert.Equal(before, Snapshot(host.Root));
        Assert.Same(recordingProvider, RecordingPreflightChecker.StorageCapacityProvider);
        Assert.Equal(0, host.BackendStarts);
        Assert.True(host.Provider.LastProbe!.RetirementCompletion.IsCompletedSuccessfully);
        Assert.True(host.Server.Stop(Bound));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public async Task CapabilityAndQueryRequireWiringReadinessAndAppDesktop(bool configured, bool ready, bool interactive)
    {
        using var host = new Host("tray", _ => RealProvider("App"), configured: configured, ready: ready, interactive: interactive);
        Assert.False((await host.Capability()).GetProperty("supported").GetBoolean());
        AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "not_ready");
        Assert.Null(host.Provider.LastProbe);
        Assert.False(Directory.Exists(host.Videos));
    }

    [Fact]
    public async Task AuthenticationAndBodyFramingRejectBeforeAnyProbe()
    {
        using var host = new Host();
        AssertFailure(await host.Send(auth: false), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        AssertFailure(await host.Send(key: "wrong"), HttpStatusCode.Forbidden, "FORBIDDEN");
        AssertFailure(await host.Send(HttpMethod.Post), HttpStatusCode.MethodNotAllowed, "METHOD_NOT_ALLOWED");
        AssertFailure(await host.Send(suffix: "?path=C%3A%5C"), HttpStatusCode.BadRequest, "INVALID_ARGUMENT");
        AssertFailure(await host.Send(suffix: "?"), HttpStatusCode.BadRequest, "INVALID_ARGUMENT");
        AssertFailure(await host.Send(body: "{}"), HttpStatusCode.BadRequest, "INVALID_ARGUMENT");
        foreach (var headers in new[]
        {
            "Content-Length: -0\r\n", "Content-Length: 0\r\nContent-Length: 0\r\n",
            "Transfer-Encoding: chunked\r\n", "Content-Length: 2147483647\r\n"
        })
            Assert.Contains("HTTP/1.1 400", await host.Raw(headers));
        ApiKeyAuth.ResetForTesting(host.Root);
        AssertFailure(await host.Send(auth: false), HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Null(host.Provider.LastProbe);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("missing-bytes")]
    [InlineData("wrong-path")]
    [InlineData("remote-identity")]
    [InlineData("extra-sample")]
    [InlineData("duplicate-bytes")]
    [InlineData("malformed")]
    public async Task StrictProbeProtocolDistinguishesExplicitZeroFromUntrustedSamples(string kind)
    {
        using var host = new Host(provider: root =>
        {
            var path = Path.Combine(root, "Videos");
            var good = JsonSerializer.Serialize(new { Path = path, VolumeIdentity = Volume, AvailableBytes = 0L });
            var reply = kind switch
            {
                "zero" => "[" + good + "]",
                "negative" => JsonSerializer.Serialize(new[] { new { Path = path, VolumeIdentity = Volume, AvailableBytes = -1L } }),
                "missing-bytes" => JsonSerializer.Serialize(new[] { new { Path = path, VolumeIdentity = Volume } }),
                "wrong-path" => JsonSerializer.Serialize(new[] { new { Path = root, VolumeIdentity = Volume, AvailableBytes = 0L } }),
                "remote-identity" => JsonSerializer.Serialize(new[] { new { Path = path, VolumeIdentity = @"\\server\share\", AvailableBytes = 0L } }),
                "extra-sample" => "[" + good + "," + good + "]",
                "duplicate-bytes" => "[" + good.Insert(good.Length - 1, ",\"AvailableBytes\":0") + "]",
                _ => "not json"
            };
            return new ProcessStorageCapacityProvider
            {
                ExecutablePath = FakeExe,
                HooksForTests = new StorageProbeHooks
                {
                    BeforeStart = info => info.Environment["AGENT_RECORDER_FAKE_STORAGE_REPLY"] = reply
                }
            };
        });
        var before = Snapshot(host.Root);
        var response = await host.Send();
        if (kind == "zero")
        {
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Equal(0, response.Json.GetProperty("data").GetProperty("available_bytes").GetInt64());
        }
        else AssertFailure(response, HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "sample_unavailable");
        Assert.True(await host.Service.WaitForIdleAsync());
        Assert.True(host.Provider.LastProbe!.RetirementCompletion.IsCompletedSuccessfully);
        Assert.Equal(before, Snapshot(host.Root));
        Assert.False(Directory.Exists(host.Videos));
    }

    [Fact]
    public async Task FileAtVideosReturnsUnavailableWithoutReplacingItAndKnownFailureAllowsAnotherQuery()
    {
        using var host = new Host();
        File.WriteAllText(host.Videos, "owned file at directory boundary");
        var before = Snapshot(host.Root);
        for (var n = 0; n < 2; n++)
        {
            var prior = host.Provider.LastProbe;
            AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "sample_unavailable");
            Assert.NotSame(prior, host.Provider.LastProbe);
            Assert.True(await host.Service.WaitForIdleAsync());
        }
        Assert.Equal(before, Snapshot(host.Root));
        Assert.False(Directory.Exists(host.Videos));
    }

    [Fact]
    public async Task NonlocalRootNeverFallsBackToTheHostDataVolume()
    {
        using var host = new Host(managedRoot: @"\\127.0.0.1\AgentRecorder407Missing_" + Guid.NewGuid().ToString("N"));
        var before = Snapshot(host.Root);
        AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE");
        Assert.True(await host.Service.WaitForIdleAsync().WaitAsync(Bound));
        Assert.Equal(before, Snapshot(host.Root));
        Assert.False(Directory.Exists(host.Videos));
    }

    [Fact]
    public async Task ExistingLocalJunctionAtVideosUsesTheSupportedProbe()
    {
        using var host = new Host();
        var target = Path.Combine(host.Root, "owned-junction-target");
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "marker.txt");
        File.WriteAllText(marker, "owned junction target");
        var command = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        command.ArgumentList.Add("-NoProfile");
        command.ArgumentList.Add("-Command");
        command.ArgumentList.Add($"New-Item -ErrorAction Stop -ItemType Junction -Path '{host.Videos.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
        using var setup = Process.Start(command)!;
        await setup.WaitForExitAsync().WaitAsync(Bound);
        Assert.True(setup.ExitCode == 0, await setup.StandardError.ReadToEndAsync());
        var before = Snapshot(host.Root);
        Assert.Equal(WindowsStorageCapacityProbe.Query(target).VolumeIdentity, WindowsStorageCapacityProbe.Query(host.Videos).VolumeIdentity);
        Assert.Equal(HttpStatusCode.OK, (await host.Send()).Status);
        Assert.Equal(before, Snapshot(host.Root));
        Assert.Equal("owned junction target", File.ReadAllText(marker));
        Assert.True(File.GetAttributes(host.Videos).HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public async Task TimeoutLeavesLiveProbeAndIdleExitBusyUntilExactRetirement()
    {
        var entered = Signal();
        var releaseRead = Signal();
        var killEntered = Signal();
        using var releaseKill = new ManualResetEventSlim();
        var calls = 0;
        using var host = new Host(provider: _ => RealProvider("Headless", new StorageProbeHooks
        {
            BeforeRead = () =>
            {
                if (Interlocked.Increment(ref calls) != 1) return Task.CompletedTask;
                entered.TrySetResult();
                return releaseRead.Task;
            },
            Kill = process =>
            {
                killEntered.TrySetResult();
                if (!releaseKill.Wait(Bound)) throw new TimeoutException("Owned probe kill gate.");
                if (!process.HasExited) process.Kill();
            }
        }));
        try
        {
            var started = Stopwatch.StartNew();
            var pending = host.Raw();
            await entered.Task.WaitAsync(Bound);
            AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "probe_busy");
            var response = await pending.WaitAsync(Bound);
            Assert.Contains("HTTP/1.1 503", response);
            Assert.Contains("\"reason_code\":\"timeout\"", response);
            Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(1800), TimeSpan.FromSeconds(5));
            await killEntered.Task.WaitAsync(Bound);
            var exact = host.Provider.LastProbe!;
            Assert.False(exact.Process.HasExited);
            Assert.False(exact.RetirementCompletion.IsCompleted);
            Assert.False(host.Service.WaitForIdleAsync().IsCompleted);
            AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "probe_busy");
            AssertFailure(await host.Shutdown(), HttpStatusCode.Conflict, "HOST_BUSY", "storage_capacity_probe_pending");
            Assert.Equal(1, calls);
            Assert.Equal(0, host.ExitPosts);

            releaseRead.TrySetResult();
            releaseKill.Set();
            Assert.True(await host.Service.WaitForIdleAsync().WaitAsync(Bound));
            Assert.True(exact.ExitCompletion.IsCompletedSuccessfully);
            Assert.True(exact.RetirementCompletion.IsCompletedSuccessfully);
            Assert.Contains("HTTP/1.1 200", await host.Raw());
            Assert.Equal(2, calls);
            Assert.Equal(HttpStatusCode.Accepted, (await host.Shutdown()).Status);
            await host.ExitPosted.Task.WaitAsync(Bound);
        }
        finally { releaseRead.TrySetResult(); releaseKill.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFinKeepsTheSampleAndResetCancelsOnlyItsOwnedProbe(bool reset)
    {
        var entered = Signal();
        var release = Signal();
        var killed = Signal();
        var fin = Signal();
        using var host = new Host(provider: _ => RealProvider("Headless", new StorageProbeHooks
        {
            BeforeRead = () => { entered.TrySetResult(); return release.Task; },
            Kill = p => { p.Kill(); killed.TrySetResult(); }
        }));
        host.Server.ArtifactTransportObservedForTests = observedFin => { if (observedFin) fin.TrySetResult(); };
        using var client = await host.OpenRaw();
        var stream = client.GetStream();
        try
        {
            await entered.Task.WaitAsync(Bound);
            var lifetime = host.Provider.LastProbe!;
            if (reset)
            {
                client.Client.LingerState = new LingerOption(true, 0);
                client.Client.Close(0);
                await killed.Task.WaitAsync(TimeSpan.FromMilliseconds(1500));
                Assert.False(lifetime.RetirementCompletion.IsCompleted);
                AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "probe_busy");
                release.TrySetResult();
            }
            else
            {
                client.Client.Shutdown(SocketShutdown.Send);
                await fin.Task.WaitAsync(Bound);
                Assert.False(killed.Task.IsCompleted);
                release.TrySetResult();
                using var reader = new StreamReader(stream);
                Assert.Contains("HTTP/1.1 200", await reader.ReadToEndAsync().WaitAsync(Bound));
                Assert.False(killed.Task.IsCompleted);
            }
            Assert.True(await host.Service.WaitForIdleAsync().WaitAsync(Bound));
            Assert.True(lifetime.RetirementCompletion.IsCompletedSuccessfully);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task CancelledRequestDoesNotAcquireTheProbeSlot()
    {
        using var host = new Host();
        var error = await Assert.ThrowsAsync<ApiException>(() => host.Service.ReadAsync(new CancellationToken(true)));
        Assert.Equal(503, error.Status);
        Assert.Null(host.Provider.LastProbe);
        Assert.True(await host.Service.WaitForIdleAsync());
        Assert.Equal(HttpStatusCode.OK, (await host.Send()).Status);
    }

    [Fact]
    public async Task StopDrainRequiresDisposalEvenAfterTheOwnedProcessHasExited()
    {
        var entered = Signal();
        var release = Signal();
        using var host = new Host(provider: _ => RealProvider("Headless", new StorageProbeHooks
        {
            BeforeDispose = () => { entered.TrySetResult(); return release.Task; }
        }));
        var pending = host.Send();
        try
        {
            await entered.Task.WaitAsync(Bound);
            var lifetime = host.Provider.LastProbe!;
            Assert.True(lifetime.ExitCompletion.IsCompletedSuccessfully);
            Assert.False(lifetime.RetirementCompletion.IsCompleted);
            Assert.False(await Task.Run(() => host.Server.Stop(TimeSpan.FromMilliseconds(250))).WaitAsync(Bound));
            Assert.False(lifetime.RetirementCompletion.IsCompleted);
            release.TrySetResult();
            Assert.True(await host.Service.WaitForIdleAsync().WaitAsync(Bound));
            Assert.True(host.Server.Stop(Bound));
            Assert.True(lifetime.RetirementCompletion.IsCompletedSuccessfully);
        }
        finally
        {
            release.TrySetResult();
            try { await pending.WaitAsync(Bound); } catch (HttpRequestException) { }
        }
    }

    [Fact]
    public async Task UnknownRetirementBlocksReplacementAndSafeShutdownWithoutAccumulatingProbes()
    {
        var starts = 0;
        using var host = new Host(provider: _ => RealProvider("Headless", new StorageProbeHooks
        {
            Started = _ => Interlocked.Increment(ref starts),
            BeforeDispose = () => throw new IOException("Private synthetic retirement error")
        }));
        var first = await host.Send();
        AssertFailure(first, HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "sample_unavailable");
        Assert.DoesNotContain("Private synthetic retirement error", first.Json.GetRawText());
        var lifetime = host.Provider.LastProbe!;
        Assert.True(lifetime.ExitCompletion.IsCompletedSuccessfully);
        Assert.NotNull(lifetime.CleanupFailure);
        Assert.False(await host.Service.WaitForIdleAsync());
        var replacement = new ManagedVideoVolumeCapacity(host.Root, RealProvider("Headless"), host.Admission);
        var error = await Assert.ThrowsAsync<ApiException>(() => replacement.ReadAsync(default));
        Assert.Equal("RETENTION_STORAGE_UNAVAILABLE", error.Code);
        for (var n = 0; n < 3; n++)
            AssertFailure(await host.Send(), HttpStatusCode.ServiceUnavailable, "RETENTION_STORAGE_UNAVAILABLE", "probe_retirement_unknown");
        AssertFailure(await host.Shutdown(), HttpStatusCode.Conflict, "HOST_BUSY", "storage_capacity_probe_retirement_unknown");
        Assert.Same(lifetime, host.Provider.LastProbe);
        Assert.Equal(1, starts);
        Assert.Equal(0, host.ExitPosts);
        Assert.False(host.Server.Stop(Bound));
    }

    private static ProcessStorageCapacityProvider RealProvider(string program, StorageProbeHooks? hooks = null) =>
        new() { ExecutablePath = () => HostExe(program), HooksForTests = hooks };

    private static string HostExe(string program)
    {
        var target = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = target.Parent!;
        var flavor = configuration.Parent!.Name;
        var output = flavor == "bin" ? Path.Combine("bin", configuration.Name) : Path.Combine("bin", flavor, configuration.Name);
        return Path.Combine(TestHelper.ProjectRoot, "src", "AgentRecorder." + program, output, target.Name, $"AgentRecorder.{program}.exe");
    }

    private static string FakeExe()
    {
        var target = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = target.Parent!;
        var artifact = Path.Combine(configuration.Parent!.FullName, "AgentRecorder.AudioHelper.Fake", target.Name, "AgentRecorder.AudioHelper.Fake.exe");
        return File.Exists(artifact) ? artifact : Path.Combine(TestHelper.ProjectRoot, "tests", "AgentRecorder.AudioHelper.Fake",
            "bin", configuration.Name, target.Name, "AgentRecorder.AudioHelper.Fake.exe");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions
    {
        RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint
    }).OrderBy(p => p, StringComparer.Ordinal).Select(p => Directory.Exists(p) ? p :
        p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))).ToArray();

    private static void AssertFailure((HttpStatusCode Status, JsonElement Json) response, HttpStatusCode status,
        string code, string? reason = null)
    {
        Assert.Equal(status, response.Status);
        var error = response.Json.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        if (reason is not null) Assert.Equal(reason, error.GetProperty("details").GetProperty("reason_code").GetString());
    }

    private sealed class Host : IDisposable
    {
        private readonly RuntimeReadiness _readiness;
        private readonly RecordingEngine _engine;
        private readonly HttpClient _http;
        private readonly string _key;
        private readonly string _endpoint;
        private readonly string _capability;
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "task407-capacity-" + Guid.NewGuid().ToString("N"));
        internal string Videos => Path.Combine(Root, "Videos");
        internal readonly ProcessStorageCapacityProvider Provider;
        internal readonly ManagedVideoVolumeCapacity.Admission Admission = new();
        internal readonly ManagedVideoVolumeCapacity Service;
        internal readonly AvStagingInventory? Staging;
        internal readonly ApiServer Server;
        internal readonly TaskCompletionSource ExitPosted = Signal();
        internal int ExitPosts;
        internal int BackendStarts;

        internal Host(string mode = "headless", Func<string, ProcessStorageCapacityProvider>? provider = null,
            bool configured = true, bool ready = true, bool interactive = true, string? managedRoot = null, bool staging = false)
        {
            _endpoint = staging ? "/api/v1/retention/staging" : Endpoint;
            _capability = staging ? "retention_staging" : "retention_storage";
            Directory.CreateDirectory(Root);
            ApiKeyAuth.InitializeForTesting(Root);
            _key = ApiKeyAuth.CurrentApiKey;
            var desktop = new InteractiveDesktopObservation(1, 1, "owned", 1, "owned", "WinSta0", "Default", "Default", "");
            _readiness = new RuntimeReadiness(mode, 0, interactive ? desktop : null, @"Local\AgentRecorderTask407-" + Guid.NewGuid().ToString("N"));
            var audit = new RetentionSharedOutputPathTests.QuietAudit();
            _engine = new RecordingEngine(audit);
            _engine.BackendFactory = _ => { Interlocked.Increment(ref BackendStarts); throw new InvalidOperationException("Capture unavailable in this fixture."); };
            var tray = new DeletionRequestFixture.Tray(mode == "headless");
            _engine.SetTray(tray);
            Provider = provider?.Invoke(Root) ?? RealProvider("Headless");
            Service = new ManagedVideoVolumeCapacity(managedRoot ?? Root, Provider, Admission);
            Server = new ApiServer(_engine, audit, tray, _readiness, null, null, null, null, null, null, null, null, 0);
            if (staging)
            {
                Staging = new AvStagingInventory(managedRoot ?? Root, Provider, new AvStagingInventory.Admission());
                if (configured) Server.SetRetentionStaging(Staging);
            }
            else if (configured) Server.SetRetentionStorage(Service);
            Server.ConfigureHostShutdown(() => null, () => { Interlocked.Increment(ref ExitPosts); ExitPosted.TrySetResult(); });
            Server.Start();
            if (ready) _readiness.MarkReady();
            _http = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{Server.BoundPort}"), Timeout = Bound
            };
        }

        internal Task<(HttpStatusCode Status, JsonElement Json)> Send(HttpMethod? method = null, string suffix = "",
            bool auth = true, string? key = null, string? body = null) =>
            SendRequest(method ?? HttpMethod.Get, _endpoint + suffix, auth, key, body);

        internal Task<(HttpStatusCode Status, JsonElement Json)> Shutdown() =>
            SendRequest(HttpMethod.Post, "/api/v1/host/shutdown", true, null, "{}");

        internal async Task<JsonElement> Capability() =>
            (await SendRequest(HttpMethod.Get, "/api/v1/capabilities", true, null, null))
                .Json.GetProperty("data").GetProperty(_capability);

        private async Task<(HttpStatusCode Status, JsonElement Json)> SendRequest(HttpMethod method, string path,
            bool auth, string? key, string? body)
        {
            using var request = new HttpRequestMessage(method, path);
            if (auth) request.Headers.Add("X-Agent-Recorder-Key", key ?? _key);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode, json.RootElement.Clone());
        }

        internal async Task<TcpClient> OpenRaw(string headers = "")
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, Server.BoundPort);
                var request = $"GET {_endpoint} HTTP/1.1\r\nHost: localhost\r\nX-Agent-Recorder-Key: {_key}\r\n{headers}Connection: close\r\n\r\n";
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(request));
                return client;
            }
            catch { client.Dispose(); throw; }
        }

        internal async Task<string> Raw(string headers = "")
        {
            using var client = await OpenRaw(headers);
            using var reader = new StreamReader(client.GetStream());
            return await reader.ReadToEndAsync().WaitAsync(Bound);
        }

        public void Dispose()
        {
            Server.Stop(Bound);
            _http.Dispose();
            _engine.Dispose();
            _readiness.Dispose();
            ApiKeyAuth.ResetForTesting(null);
        }
    }
}
