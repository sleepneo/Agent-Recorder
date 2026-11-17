using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AgentRecorder.Mcp.Tests;

public sealed class Fixture : IAsyncLifetime
{
    public static readonly string Repo = FindRepo();
    public string Root { get; } = Path.Combine(Repo, ".local-data", "task430", "tests-" + Guid.NewGuid().ToString("N"));
    public string Package => Path.Combine(Root, "external portable 包");
    public string Plugin => Path.Combine(Root, "plugin-build", "agent-recorder-bootstrap");
    public string Adapter => Environment.GetEnvironmentVariable("AGENT_RECORDER_MCP_TEST_PLUGIN_ROOT") is { } plugin
        ? Path.Combine(plugin, "server", "AgentRecorder.Mcp.exe")
        : Path.Combine(Repo, "src", "AgentRecorder.Mcp", "bin", Configuration, "net8.0-windows", "AgentRecorder.Mcp.exe");
    public static string Configuration => AppContext.BaseDirectory.Contains("\\Release\\") ? "Release" : "Debug";
    public string NewPath(string suffix) => Path.Combine(Root, Guid.NewGuid().ToString("N"), suffix);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        foreach (var name in new[] { "Cli", "App" })
        {
            var target = Path.Combine(Package, "AgentRecorder." + name);
            var project = Path.Combine(Repo, "tests", "AgentRecorder.Mcp.Fake" + name, "AgentRecorder.Mcp.Fake" + name + ".csproj");
            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "publish", project, "-c", Configuration, "-r", "win-x64", "--self-contained", "true",
                    "-o", target, "--nologo", "-p:DebugType=none", "-p:UseSharedCompilation=false" }
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        File.WriteAllText(Path.Combine(Package, "AgentRecorder.App", "ffmpeg.exe"), "synthetic dependency, not executed");
        var buildStart = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-File", Path.Combine(Repo, "scripts", "build-codex-plugin.ps1"), "-OutputDirectory", Path.GetDirectoryName(Plugin)! }
        };
        buildStart.Environment["UseSharedCompilation"] = "false";
        using var build = Process.Start(buildStart)!;
        var buildOutput = build.StandardOutput.ReadToEndAsync(); var buildError = build.StandardError.ReadToEndAsync();
        await build.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
        Assert.True(build.ExitCode == 0, await buildOutput + await buildError);
    }

    public Task DisposeAsync()
    {
        // Only this uniquely named, verified workspace fixture root. Never use process kills.
        var allowed = Path.GetFullPath(Path.Combine(Repo, ".local-data", "task430")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(allowed, Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(Root, recursive: true);
        return Task.CompletedTask;
    }

    public static void ExistingData(string path)
    {
        Directory.CreateDirectory(Path.Combine(path, "state"));
        Directory.CreateDirectory(Path.Combine(path, "config"));
        File.WriteAllBytes(Path.Combine(path, "state", "agent-recorder.db"), "SQLite format 3\0fixture only"u8.ToArray());
        File.WriteAllText(Path.Combine(path, "config", "api-key.txt"), "SECRET-fixture-key");
    }

    public void Mode(string value) => File.WriteAllText(Path.Combine(Package, "fixture-mode.txt"), value);
    public async Task<Session> Connect(string binding, string? executable = null, string? cwd = null)
        => await Session.Start(executable ?? Adapter, binding, cwd ?? NewPath("unrelated cwd"));

    public static async Task Eventually(Func<bool> condition, int timeoutMs = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < timeoutMs) await Task.Delay(40);
        Assert.True(condition(), "Synthetic process did not reach its expected normal state before the deadline.");
    }

    private static string FindRepo()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AgentRecorder.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository not found");
    }
}

public sealed class Session : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly CaptureStream stdout;
    public McpClient Client { get; private set; } = null!;
    public int Pid => process.Id;
    internal object ObserveReadFailureOwner() => AgentRecorder.LifecycleTests.LifecycleObservation.Capture(process, "operation_read_failure");
    public string Wire => Encoding.UTF8.GetString(stdout.Bytes.ToArray());
    private bool closed;

    private Session(Process process)
    {
        this.process = process;
        stderr = process.StandardError.ReadToEndAsync();
        stdout = new CaptureStream(process.StandardOutput.BaseStream);
    }

    public static async Task<Session> Start(string executable, string binding, string cwd, IDictionary<string, string>? environment = null)
    {
        Assert.True(Path.IsPathFullyQualified(binding));
        Assert.StartsWith(Path.Combine(Fixture.Repo, ".local-data", "task430") + "\\", binding, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(cwd);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "--binding-file", binding }
        };
        if (environment != null) foreach (var entry in environment) start.Environment[entry.Key] = entry.Value;
        var process = Process.Start(start)!;
        var session = new Session(process);
        // SDK owns protocol; tests own the OS process. This transport never kills a child
        // to mask EOF/lifecycle bugs as StdioClientTransport's forced fallback could.
        session.Client = await McpClient.CreateAsync(new StreamClientTransport(process.StandardInput.BaseStream, session.stdout),
            cancellationToken: new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        return session;
    }

    public async Task<JsonElement> Call(string name, Dictionary<string, object?>? args = null, CancellationToken ct = default)
    {
        var result = await Client.CallToolAsync(name, args, cancellationToken: ct);
        Assert.NotNull(result.StructuredContent);
        var value = result.StructuredContent!.Value;
        Assert.Equal(!value.GetProperty("ok").GetBoolean(), result.IsError ?? false);
        Assert.DoesNotContain("SECRET-fixture-key", value.GetRawText());
        return value;
    }

    public async Task Eof()
    {
        if (closed) return;
        closed = true;
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, process.ExitCode);
    }

    public async Task<bool> DisposeSdkWithoutClosingCallerStreams()
    {
        await Client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        return process.HasExited;
    }

    public async ValueTask DisposeAsync()
    {
        await Eof();
        await Client.DisposeAsync();
        Assert.Equal("", await stderr);
        foreach (var line in Wire.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("2.0", document.RootElement.GetProperty("jsonrpc").GetString());
        }
        Assert.DoesNotContain("SECRET-fixture-key", Wire);
        process.Dispose();
    }
}

internal sealed class CaptureStream(Stream source) : Stream
{
    public MemoryStream Bytes { get; } = new();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    { var read = source.Read(buffer, offset, count); Bytes.Write(buffer, offset, read); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { var read = await source.ReadAsync(buffer, cancellationToken); Bytes.Write(buffer.Span[..read]); return read; }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed class ProtocolTests(Fixture fixture, ITestOutputHelper output) : IClassFixture<Fixture>
{
    private Dictionary<string, object?> Bind(string data, bool initialize = false, string? package = null) => new()
    { ["package_root"] = package ?? fixture.Package, ["data_dir"] = data, ["initialize_new_data_directory"] = initialize };
    private static string Reason(JsonElement value) => value.GetProperty("reason_code").GetString()!;

    [Fact]
    public async Task InitializeListAndReadAreReadOnlyAndExposeExactlySixStrictTools()
    {
        var binding = fixture.NewPath("not-created/binding.json");
        await using var session = await fixture.Connect(binding);
        var list = await session.Client.ListToolsAsync();
        Assert.Equal(new[] { "recorder_binding_get", "recorder_binding_set", "recorder_host_ensure", "recorder_operation_get", "recorder_record_region", "recorder_recording_stop" }, list.Select(t => t.Name).Order());
        foreach (var tool in list) Assert.False(tool.JsonSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("binding_unconfigured", Reason(await session.Call("recorder_binding_get")));
        Assert.Equal("binding_unconfigured", Reason(await session.Call("recorder_host_ensure")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(binding)));
        Assert.Equal("invalid_arguments", Reason(await session.Call("recorder_binding_get", new() { ["url"] = "http://evil" })));
        Assert.Equal("invalid_arguments", Reason(await session.Call("recorder_binding_set", new() { ["package_root"] = 42 })));
    }

    [Fact]
    public async Task ExistingBindingSurvivesCwdAndAdapterRelocationAndFailedRebind()
    {
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("existing 数据"); Fixture.ExistingData(data);
        await using (var session = await fixture.Connect(binding))
        {
            Assert.Equal("binding_saved", Reason(await session.Call("recorder_binding_set", Bind(data))));
            var original = File.ReadAllText(binding);
            Assert.Equal("package_missing", Reason(await session.Call("recorder_binding_set", Bind(data, package: fixture.NewPath("missing")))));
            Assert.Equal(original, File.ReadAllText(binding));
            Assert.DoesNotContain("SECRET-fixture-key", original);
        }
        var cache = fixture.NewPath("plugin-cache/version-0.1.1/server"); Directory.CreateDirectory(cache);
        foreach (var path in Directory.GetFiles(Path.GetDirectoryName(fixture.Adapter)!)) File.Copy(path, Path.Combine(cache, Path.GetFileName(path)));
        var cwd = fixture.NewPath("different-project");
        await using (var moved = await fixture.Connect(binding, Path.Combine(cache, "AgentRecorder.Mcp.exe"), cwd))
        {
            var result = await moved.Call("recorder_binding_get");
            Assert.Equal("binding_valid", Reason(result));
            Assert.Equal(data, result.GetProperty("binding").GetProperty("data_dir").GetString());
            Assert.Empty(Directory.GetFileSystemEntries(cwd));
            Assert.False(File.Exists(Path.Combine(cache, "binding.json")));
        }
    }

    [Theory]
    [InlineData("relative", "path_unsupported")]
    [InlineData("C:relative", "path_unsupported")]
    [InlineData("\\\\server\\share", "path_unsupported")]
    [InlineData("\\\\?\\C:\\extended", "path_unsupported")]
    [InlineData("C:\\tmp\\..\\escape", "path_unsupported")]
    [InlineData("C:\\tmp\\nul", "path_unsupported")]
    [InlineData("C:\\tmp\\trailing.", "path_unsupported")]
    [InlineData("C:\\tmp\\file:stream", "path_unsupported")]
    public async Task RejectAmbiguousPaths(string data, string expected)
    {
        await using var session = await fixture.Connect(fixture.NewPath("binding.json"));
        Assert.Equal(expected, Reason(await session.Call("recorder_binding_set", Bind(data, true))));
    }

    [Fact]
    public async Task RejectReparseParentWithoutFollowingIt()
    {
        var target = fixture.NewPath("target"); Directory.CreateDirectory(target);
        var link = fixture.NewPath("junction"); Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        // Junctions do not need symlink privilege. No external target or user path.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            ArgumentList = { "/c", "mklink", "/J", link, target } })!;
        await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
        try
        {
            await using var session = await fixture.Connect(fixture.NewPath("binding.json"));
            Assert.Equal("path_reparse_point", Reason(await session.Call("recorder_binding_set", Bind(Path.Combine(link, "new"), true))));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("{", "binding_corrupt")]
    [InlineData("{\"schema_version\":2}", "binding_schema_unsupported")]
    [InlineData("{\"schema_version\":1,\"schema_version\":1}", "binding_corrupt")]
    public async Task CorruptAndUnknownBindingsAreNotReplaced(string content, string reason)
    {
        var binding = fixture.NewPath("binding.json"); Directory.CreateDirectory(Path.GetDirectoryName(binding)!); File.WriteAllText(binding, content);
        var data = fixture.NewPath("data"); Fixture.ExistingData(data);
        await using var session = await fixture.Connect(binding);
        Assert.Equal(reason, Reason(await session.Call("recorder_binding_get")));
        Assert.Equal(reason, Reason(await session.Call("recorder_binding_set", Bind(data))));
        Assert.Equal(content, File.ReadAllText(binding));
    }

    [Fact]
    public async Task DependenciesAndVersionsAreCheckedWithoutExecution()
    {
        var dependency = Path.Combine(fixture.Package, "AgentRecorder.Cli", "hostpolicy.dll");
        var moved = dependency + ".held"; File.Move(dependency, moved);
        await using var session = await fixture.Connect(fixture.NewPath("binding.json"));
        try { Assert.Equal("package_dependency_missing", Reason(await session.Call("recorder_binding_set", Bind(fixture.NewPath("data"), true)))); }
        finally { File.Move(moved, dependency); }
        var assembly = Path.Combine(fixture.Package, "AgentRecorder.App", "AgentRecorder.App.dll");
        var saved = File.ReadAllBytes(assembly);
        try
        {
            File.Copy(typeof(McpClient).Assembly.Location, assembly, true);
            Assert.Equal("package_identity_mismatch", Reason(await session.Call("recorder_binding_set", Bind(fixture.NewPath("data"), true))));
        }
        finally { File.WriteAllBytes(assembly, saved); }
    }

    [Fact]
    public async Task RealMismatchedProductVersionIsRejected()
    {
        var target = fixture.NewPath("wrong-version");
        using var build = Process.Start(new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "build", Path.Combine(Fixture.Repo, "tests", "AgentRecorder.Mcp.FakeApp", "AgentRecorder.Mcp.FakeApp.csproj"),
                "-c", Fixture.Configuration, "-o", target, "-p:Version=0.1.26", "--nologo" }
        })!;
        var stdout = build.StandardOutput.ReadToEndAsync(); var stderr = build.StandardError.ReadToEndAsync();
        await build.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(build.ExitCode == 0, await stdout + await stderr);
        var executable = Path.Combine(fixture.Package, "AgentRecorder.App", "AgentRecorder.App.exe");
        var assembly = Path.ChangeExtension(executable, ".dll");
        var savedExe = File.ReadAllBytes(executable); var savedDll = File.ReadAllBytes(assembly);
        try
        {
            File.Copy(Path.Combine(target, "AgentRecorder.App.dll"), assembly, true);
            File.Copy(Path.Combine(target, "AgentRecorder.App.exe"), executable, true);
            await using var session = await fixture.Connect(fixture.NewPath("binding.json"));
            Assert.Equal("product_version_unsupported", Reason(await session.Call("recorder_binding_set", Bind(fixture.NewPath("new"), true))));
        }
        finally { File.WriteAllBytes(executable, savedExe); File.WriteAllBytes(assembly, savedDll); }
    }

    [Fact]
    public async Task PublishedPluginSchemasReferencesAndSelfContainedMcpActuallyWork()
    {
        var plugin = Environment.GetEnvironmentVariable("AGENT_RECORDER_MCP_TEST_PLUGIN_ROOT") ?? fixture.Plugin;
        Assert.True(Path.GetFullPath(plugin).StartsWith(Path.Combine(Fixture.Repo, ".local-data", "task430") + "\\", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(plugin), Path.Combine(Fixture.Repo, ".local-data", "task432", "development-stop", "agent-recorder-bootstrap"), StringComparison.OrdinalIgnoreCase));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin, "plugin.json")));
        Assert.Equal("0.3.2", manifest.RootElement.GetProperty("version").GetString());
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin, "mcp.json")));
        var servers = config.RootElement.GetProperty("mcpServers"); Assert.Single(servers.EnumerateObject());
        var server = servers.GetProperty("agent-recorder-bootstrap");
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        var launch = PluginStdioLaunch.Read(plugin);
        Assert.Equal("./server/AgentRecorder.Mcp.exe", server.GetProperty("command").GetString());
        Assert.True(File.Exists(launch.Command));
        Assert.Equal(File.ReadAllText(Path.Combine(Fixture.Repo, "integrations", "codex", "plugin", "skills", "recorder-connection", "SKILL.md")),
            File.ReadAllText(Path.Combine(plugin, "skills", "recorder-connection", "SKILL.md")));
        var binding = fixture.NewPath("plugin-binding/binding.json");
        var data = fixture.NewPath("plugin-existing"); Fixture.ExistingData(data); fixture.Mode("reuse");
        await using var session = await fixture.Connect(binding, launch.Command, launch.WorkingDirectory);
        Assert.Equal("0.3.2", session.Client.ServerInfo!.Version);
        Assert.Equal(6, (await session.Client.ListToolsAsync()).Count);
        Assert.Equal("binding_saved", Reason(await session.Call("recorder_binding_set", Bind(data))));
        Assert.Equal("binding_valid", Reason(await session.Call("recorder_binding_get")));
        Assert.Equal("host_ready_cli_verified", Reason(await session.Call("recorder_host_ensure")));
        foreach (var path in Directory.EnumerateFiles(plugin, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(Path.GetExtension(path), new[] { ".pdb", ".db", ".key", ".cs", ".ps1" });
            if (Path.GetExtension(path) is ".md" or ".json")
            {
                Assert.DoesNotContain(Fixture.Repo, File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("SECRET-fixture-key", File.ReadAllText(path));
            }
        }
    }

    [Fact]
    public async Task NewInitializationIsExplicitAndNeverRecreatesLostOrUnknownData()
    {
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("new data");
        await using var session = await fixture.Connect(binding);
        Assert.Equal("data_directory_missing", Reason(await session.Call("recorder_binding_set", Bind(data))));
        Assert.False(Directory.Exists(data));
        Assert.Equal("binding_saved_pending_initialization", Reason(await session.Call("recorder_binding_set", Bind(data, true))));
        Assert.Empty(Directory.GetFileSystemEntries(data));
        Directory.Delete(data);
        Assert.Equal("data_directory_missing", Reason(await session.Call("recorder_binding_get")));
        Assert.Equal("new_data_directory_required", Reason(await session.Call("recorder_binding_set", Bind(data, true))));
        Assert.False(Directory.Exists(data));
        Directory.CreateDirectory(data);
        Assert.Equal("data_directory_replaced", Reason(await session.Call("recorder_binding_get")));
        var uncertainData = fixture.NewPath("uncertain new data");
        Assert.Equal("binding_saved_pending_initialization", Reason(await session.Call("recorder_binding_set", Bind(uncertainData, true))));
        fixture.Mode("error:INTERACTIVE_DESKTOP_REQUIRED");
        var denied = await session.Call("recorder_host_ensure");
        Assert.Equal("INTERACTIVE_DESKTOP_REQUIRED", denied.GetProperty("product_error_code").GetString());
        Assert.Equal("initialization_unknown", Reason(await session.Call("recorder_binding_get")));
        Assert.Equal("initialization_unknown", Reason(await session.Call("recorder_host_ensure")));
    }

    [Fact]
    public async Task InitializedDatabaseLossAndInvalidityFailBeforeCli()
    {
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("data"); Fixture.ExistingData(data);
        await using var session = await fixture.Connect(binding);
        await session.Call("recorder_binding_set", Bind(data));
        var db = Path.Combine(data, "state", "agent-recorder.db"); File.WriteAllText(db, "bad");
        Assert.Equal("initialized_data_invalid", Reason(await session.Call("recorder_binding_get")));
        File.Delete(db);
        Assert.Equal("initialized_data_missing", Reason(await session.Call("recorder_host_ensure")));
        Assert.False(File.Exists(db)); Assert.False(Directory.Exists(Path.Combine(data, "runtime")));
    }

    [Fact]
    public async Task ReplacedDatabaseAtTheSamePathRequiresExplicitRebind()
    {
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("data"); Fixture.ExistingData(data);
        await using var session = await fixture.Connect(binding);
        await session.Call("recorder_binding_set", Bind(data));
        var db = Path.Combine(data, "state", "agent-recorder.db");
        File.Move(db, db + ".original"); Fixture.ExistingData(data);
        Assert.Equal("initialized_data_replaced", Reason(await session.Call("recorder_binding_get")));
        Assert.Equal("initialized_data_replaced", Reason(await session.Call("recorder_host_ensure")));
        Assert.False(Directory.Exists(Path.Combine(data, "runtime")));
    }

    [Fact]
    public async Task CliReceivesExactArgumentsAndRepeatedEnsureReusesHost()
    {
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("data 含空格");
        await using var session = await fixture.Connect(binding);
        fixture.Mode("ready"); await session.Call("recorder_binding_set", Bind(data, true));
        Assert.Equal("host_ready_cli_verified", Reason(await session.Call("recorder_host_ensure")));
        fixture.Mode("reuse"); var reused = await session.Call("recorder_host_ensure");
        Assert.False(reused.GetProperty("host").GetProperty("started").GetBoolean());
        Assert.Equal("not_verified", reused.GetProperty("host").GetProperty("human_window_visibility").GetString());
        var lines = File.ReadAllLines(Path.Combine(data, "runtime", "cli-arguments.jsonl")); Assert.Equal(2, lines.Length);
        var expected = new[] { "ensure-running", "--json", "--package-root", fixture.Package, "--app",
            Path.Combine(fixture.Package, "AgentRecorder.App", "AgentRecorder.App.exe"), "--data-dir", data, "--tray", "--timeout-ms", "10000" };
        foreach (var line in lines) Assert.Equal(expected, JsonSerializer.Deserialize<string[]>(line));
    }

    [Theory]
    [InlineData("error:PROCESS_IDENTITY_MISMATCH", "product_cli_rejected")]
    [InlineData("error:DATA_DIRECTORY_MISMATCH", "product_cli_rejected")]
    [InlineData("bad-api", "cli_evidence_invalid_unknown")]
    [InlineData("bad-data", "cli_evidence_invalid_unknown")]
    public async Task ConflictsAndUnprovenReadyFailClosed(string mode, string expected)
    {
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("data"); Fixture.ExistingData(data); fixture.Mode(mode);
        await using var session = await fixture.Connect(binding); await session.Call("recorder_binding_set", Bind(data));
        Assert.Equal(expected, Reason(await session.Call("recorder_host_ensure")));
        Assert.Equal("host_start_unknown", Reason(await session.Call("recorder_host_ensure")));
        Assert.Single(File.ReadAllLines(Path.Combine(data, "runtime", "cli-arguments.jsonl")));
    }

    [Fact]
    public async Task CompetingProcessesWriteAtomicallyAndLockFailureIsBounded()
    {
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("data"); Fixture.ExistingData(data);
        await using var first = await fixture.Connect(binding); await using var second = await fixture.Connect(binding);
        var results = await Task.WhenAll(first.Call("recorder_binding_set", Bind(data)), second.Call("recorder_binding_set", Bind(data)));
        Assert.All(results, r => Assert.Equal("binding_saved", Reason(r)));
        using (var held = new FileStream(binding + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var timer = Stopwatch.StartNew();
            Assert.Equal("binding_busy", Reason(await first.Call("recorder_binding_set", Bind(data))));
            Assert.InRange(timer.ElapsedMilliseconds, 1400, 4000);
            Assert.Equal("binding_valid", Reason(await second.Call("recorder_binding_get")));
        }
        using var document = JsonDocument.Parse(File.ReadAllText(binding));
        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(binding)!, "*.tmp"));
    }

    [Theory]
    [InlineData("timeout", "cli_timeout_unknown")]
    [InlineData("flood", "cli_output_limit_unknown")]
    [InlineData("cancel", "cancel")]
    [InlineData("borrow", "eof")]
    [InlineData("disconnect", "eof-inflight")]
    public async Task ObservationEndsWithoutKillingBorrowedHost(string mode, string expected)
    {
        var binding = fixture.NewPath("binding.json"); var data = fixture.NewPath("data"); Fixture.ExistingData(data); fixture.Mode(mode);
        await using var session = await fixture.Connect(binding); await session.Call("recorder_binding_set", Bind(data));
        var timer = Stopwatch.StartNew();
        using var cancel = new CancellationTokenSource();
        var call = session.Call("recorder_host_ensure", ct: cancel.Token);
        var receipt = Path.Combine(data, "runtime", "host.json");
        await Fixture.Eventually(() => File.Exists(receipt));
        using var receiptJson = JsonDocument.Parse(File.ReadAllText(receipt));
        using var host = Process.GetProcessById(receiptJson.RootElement.GetProperty("pid").GetInt32());
        _ = host.SafeHandle;
        output.WriteLine("Synthetic Windows host in inherited Job: " + receiptJson.RootElement.GetProperty("in_job").GetBoolean());
        using var cli = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(data, "runtime", "cli-pid"))));
        _ = cli.SafeHandle;
        try
        {
            if (mode == "cancel")
            {
                cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
                await Fixture.Eventually(() => File.ReadAllText(binding).Contains("unknown"));
            }
            else if (mode == "borrow")
            {
                Assert.Equal("host_ready_cli_verified", Reason(await call));
                await session.Eof();
            }
            else if (mode == "disconnect")
            {
                await session.Eof();
                await Assert.ThrowsAnyAsync<Exception>(async () => await call);
            }
            else
            {
                Assert.Equal(expected, Reason(await call));
                if (mode == "timeout") Assert.InRange(timer.ElapsedMilliseconds, 14500, 19000);
            }
            Assert.False(host.HasExited);
            await session.Eof();
            Assert.False(host.HasExited);
            await Fixture.Eventually(() => File.Exists(Path.Combine(data, "runtime", "cli-retired")), mode == "timeout" ? 6000 : 5000);
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, cli.ExitCode);
            Assert.False(host.HasExited);
            Assert.Single(File.ReadAllLines(Path.Combine(data, "runtime", "cli-arguments.jsonl")));
        }
        finally
        {
            File.WriteAllText(Path.Combine(data, "runtime", "host-exit"), "normal-exit-request");
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, host.ExitCode);
            Assert.Equal("normal", File.ReadAllText(receipt + ".retired"));
        }
    }
}
