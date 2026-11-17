using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRecorder.LifecycleTests;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Mcp.Tests;

public sealed class HostLifecycleDiagnosticsTests(Fixture fixture, ITestOutputHelper output) : IClassFixture<Fixture>
{
    private readonly string evidenceRoot = Path.Combine(Fixture.Repo, ".local-data", "task433", Guid.NewGuid().ToString("N"));
    private const string Recording = "rec_123456abcdef";
    private string App => Path.Combine(fixture.Package, "AgentRecorder.App", "AgentRecorder.App.exe");
    private static string Status(JsonElement result) => result.GetProperty("status").GetString()!;
    private static string Reason(JsonElement result) => result.GetProperty("reason_code").GetString()!;
    private static Dictionary<string, object?> Query(string id) => new() { ["operation_id"] = id };
    private static int Count(string control, string file) => File.Exists(Path.Combine(control, file)) ? File.ReadAllLines(Path.Combine(control, file)).Length : 0;
    private static void Set(string control, string name, string value = "yes") => File.WriteAllText(Path.Combine(control, name), value);
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path))!;

    private async Task Diagnose(string name, Func<Dictionary<string, object?>, Task> action)
    {
        Directory.CreateDirectory(evidenceRoot); var path = Path.Combine(evidenceRoot, name + ".json");
        var facts = new Dictionary<string, object?> { ["schema_version"] = 1, ["case"] = name,
            ["scope"] = "private_synthetic_only", ["real_codex_exit_cause"] = "unknown",
            ["started_at_utc"] = DateTimeOffset.UtcNow, ["result"] = "running", ["ancestor_job_limits"] = "unknown" };
        try { await action(facts).WaitAsync(TimeSpan.FromSeconds(45)); facts["result"] = "verified_in_synthetic_scope"; }
        catch (Exception error)
        {
            facts["result"] = "failed_or_environment_unsupported_not_skipped";
            facts["reason_code"] = Regex.IsMatch(error.Message, "^[a-z0-9_]+_win32_[0-9]+$") ? error.Message : error.GetType().Name;
            throw;
        }
        finally
        {
            facts["finished_at_utc"] = DateTimeOffset.UtcNow;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(facts, Json.Options);
            Assert.True(bytes.Length <= 32768, "Diagnostic evidence exceeded its fixed budget.");
            File.WriteAllBytes(path, bytes); output.WriteLine("Lifecycle evidence: " + path);
        }
    }

    private Process PinHost(string data)
    {
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(data, "runtime", "ready.json")));
        var process = Process.GetProcessById(ready.RootElement.GetProperty("pid").GetInt32()); _ = process.Handle;
        Assert.Equal(ready.RootElement.GetProperty("process_started_at_utc_ticks").GetInt64(), process.StartTime.ToUniversalTime().Ticks);
        Assert.Equal(Path.GetFullPath(App), LifecycleObservation.Image(process.Handle), ignoreCase: true);
        return process;
    }
    private static void AssertNoAuthorityActions(string control)
    {
        Assert.Equal(0, Count(control, "stop-posts"));
        var routes = File.Exists(Path.Combine(control, "routes")) ? File.ReadAllLines(Path.Combine(control, "routes")) : [];
        Assert.DoesNotContain(routes, route => route.Contains("/stop") || route.Contains("confirmations/") || route.Contains("shutdown") || route.Contains("history"));
    }
    private static bool GenerationRetired(ProcessObservation identity)
    {
        try { using var process = Process.GetProcessById(identity.Pid); return process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartedTicks; }
        catch (ArgumentException) { return true; }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task NormalCliRetirementAndMcpEofOrSdkCloseDoNotStopOwnedOrBorrowedHost(bool borrowed, bool sdkClose, bool knownId)
        => await Diagnose($"{(borrowed ? "borrowed" : "new-shell")}-{(sdkClose ? "sdk-dispose-then-eof" : "stdin-eof")}-{(knownId ? "known-id" : "unknown-submit")}", async facts =>
    {
        var binding = fixture.NewPath("binding.json"); var id = Guid.NewGuid().ToString("D");
        FakeApi? borrowedApi = null; Process? host = null; string data, control;
        if (borrowed)
        {
            borrowedApi = await FakeApi.Start(fixture, observeLifecycle: true); data = borrowedApi.Data; control = borrowedApi.Control;
            File.WriteAllText(Path.Combine(fixture.Package, "lifecycle-control-path.txt"), control); fixture.Mode("lifecycle-borrow");
        }
        else
        {
            data = fixture.NewPath("data"); Fixture.ExistingData(data); control = Path.Combine(data, "lifecycle-control");
            Directory.CreateDirectory(control); fixture.Mode("lifecycle-new");
        }
        var databaseIdentity = FileIdentity.Read(Path.Combine(data, "state", "agent-recorder.db"));
        try
        {
            if (knownId) { Set(control, "selected"); Set(control, "approved"); Set(control, "state", "recording"); }
            await using var session = await fixture.Connect(binding);
            using var mcp = Process.GetProcessById(session.Pid); _ = mcp.Handle;
            facts["mcp_before"] = LifecycleObservation.Capture(mcp, "before_operation", Environment.ProcessId);
            Assert.Equal("binding_saved", Reason(await session.Call("recorder_binding_set", new() { ["package_root"] = fixture.Package, ["data_dir"] = data })));
            Assert.Equal("accepted", Status(await session.Call("recorder_record_region", Query(id))));
            await Fixture.Eventually(() => Count(control, "posts") == 1, 10000);
            host = PinHost(data);
            facts["host_after_cli_exit"] = LifecycleObservation.Capture(host, "after_cli_exit");
            Assert.Null(LifecycleObservation.Capture(host, "external_scope_check").SelfImmediateJobLimitFlags);
            facts["host_self_at_start"] = Read<ProcessObservation>(Path.Combine(control, "app-start.json"));
            var cliEvidence = Read<ProcessObservation>(Path.Combine(control, borrowed ? "cli-before-borrowed-observation.json" : "cli-before-normal-exit.json"));
            facts["cli_self"] = cliEvidence;
            await Fixture.Eventually(() => GenerationRetired(cliEvidence));
            Assert.Equal("normal", File.ReadAllText(Path.Combine(data, "runtime", "cli-retired")));
            Assert.False(host.HasExited);
            var store = new OperationStore(new BindingStore(binding));
            if (knownId)
            {
                await Fixture.Eventually(() => store.Read(id)?.RecordingId == Recording);
                Assert.Equal("recording", Status(await session.Call("recorder_operation_get", Query(id))));
            }
            else
            {
                Assert.Equal("submitting", Status(await session.Call("recorder_operation_get", Query(id))));
                Assert.Null(store.Read(id)!.RecordingId);
            }
            if (!borrowed) facts["shell_launch_semantics"] = Read<JsonElement>(Path.Combine(control, "launch.json"));
            if (sdkClose)
            {
                var exitedOnSdkDispose = await session.DisposeSdkWithoutClosingCallerStreams();
                facts["sdk_dispose_alone_mcp_retired"] = exitedOnSdkDispose;
                // StreamClientTransport uses caller-owned streams: SDK disposal
                // is a distinct event, not evidence that server stdin saw EOF.
                Assert.False(exitedOnSdkDispose); await Task.Delay(250); Assert.False(mcp.HasExited); Assert.False(host.HasExited);
                facts["host_after_sdk_dispose_before_eof"] = LifecycleObservation.Capture(host, "sdk_disposed_stdin_still_open");
            }
            await session.Eof(); facts["caller_explicitly_closed_stdin"] = true;
            await Task.Delay(500); Assert.False(host.HasExited);
            facts["host_after_mcp_retirement"] = LifecycleObservation.Capture(host, "after_mcp_retirement");
            facts["mcp_retired"] = mcp.HasExited;
            Assert.True(mcp.HasExited); Assert.Equal(1, Count(control, "posts")); AssertNoAuthorityActions(control);
            Set(control, "selected"); Set(control, "approved"); Set(control, "state", "completed");
            await Fixture.Eventually(() => File.Exists(Path.Combine(control, "native-created")));
            await using var observer = await fixture.Connect(binding);
            var result = await observer.Call("recorder_operation_get", Query(id));
            Assert.Equal(knownId ? "publication_confirmed" : "unknown", knownId ? Reason(result) : Status(result));
            if (!knownId) Assert.Null(result.GetProperty("recording_id").GetString());
            facts["restarted_observer_result"] = knownId ? "same_id_publication_confirmed" : "unknown_without_native_id_no_guess";
            Assert.Equal(1, Count(control, "posts")); AssertNoAuthorityActions(control);
            Assert.Single(File.ReadAllLines(Path.Combine(data, "runtime", "cli-arguments.jsonl")));
            Assert.Equal(databaseIdentity, FileIdentity.Read(Path.Combine(data, "state", "agent-recorder.db")));
            Assert.Empty(Directory.EnumerateFiles(data, "*.mp4", SearchOption.AllDirectories));
            facts["new_recording_posts"] = Count(control, "posts"); facts["stop_posts"] = Count(control, "stop-posts");
            facts["database_identity_unchanged"] = true;
        }
        finally
        {
            if (host == null && File.Exists(Path.Combine(control, "child-created.json")))
                facts["child_created_before_unconfirmed_readiness"] = Read<ProcessObservation>(Path.Combine(control, "child-created.json"));
            if (host != null) facts["host_before_normal_control_retirement"] = LifecycleObservation.Capture(host, "cleanup_control_signal");
            if (borrowedApi != null) await borrowedApi.DisposeAsync();
            else
            {
                Set(control, "exit", "normal");
                if (host != null) { await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(0, host.ExitCode); }
                Assert.Equal("normal", File.ReadAllText(Path.Combine(control, "retired")));
            }
            facts["host_normal_retirement_confirmed"] = true;
            host?.Dispose();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DedicatedJobCloseModelDistinguishesOwnKillOnCloseFromNormalRetirement(bool killOnClose)
        => await Diagnose(killOnClose ? "dedicated-kill-on-close" : "dedicated-no-kill-on-close", async facts =>
    {
        var data = fixture.NewPath("model-data"); Fixture.ExistingData(data);
        var control = fixture.NewPath("model-control"); Directory.CreateDirectory(control);
        using var job = new DedicatedSyntheticJob(fixture.Root, killOnClose);
        var cliPath = Path.Combine(fixture.Package, "AgentRecorder.Cli", "AgentRecorder.Cli.exe");
        Process cli;
        try { cli = job.CreateAssignedFakeCli(cliPath, App, data, control); }
        catch
        {
            facts["created_fake_cli_on_setup_failure"] = job.CreatedBeforeAssignment;
            facts["setup_abort_retirement"] = job.SetupAbortRetirement;
            throw;
        }
        using var cliLifetime = cli;
        facts["cli_verified_before_resume"] = job.AssignedBeforeResume;
        facts["owned_job_limit_flags"] = job.ReadOwnedLimitFlags();
        facts["model_launch_difference"] = "CreateProcess_style_fake_cli_child_not_product_ShellExecute_or_real_client_exit";
        Assert.Equal(killOnClose ? 0x2000u : 0u, job.ReadOwnedLimitFlags());
        Process? host = null;
        try
        {
            await Fixture.Eventually(() => File.Exists(Path.Combine(control, "model-launch.json")), 10000);
            host = PinHost(data); var ticks = host.StartTime.ToUniversalTime().Ticks;
            Assert.True(job.ContainsVerifiedFake(host, App, ticks));
            facts["host_in_this_exact_owned_job"] = true;
            facts["host_before_job_close"] = LifecycleObservation.Capture(host, "before_owned_job_close", cli.Id);
            facts["cli_self"] = Read<ProcessObservation>(Path.Combine(control, "model-cli-before-normal-exit.json"));
            var hostSelf = Read<ProcessObservation>(Path.Combine(control, "app-start.json"));
            facts["host_self"] = hostSelf;
            Assert.True(hostSelf.InAnyJob); Assert.Equal(killOnClose ? 0x2000u : 0u, hostSelf.SelfImmediateJobLimitFlags);
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(0, cli.ExitCode);
            Assert.False(host.HasExited); facts["cli_retired_before_job_close"] = true;
            AssertNoAuthorityActions(control); Assert.Equal(0, Count(control, "posts"));
            job.Dispose(); // Last non-inheritable handle to this unnamed test Job only.
            if (killOnClose)
            {
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                facts["host_exit_code"] = host.ExitCode;
                Assert.False(File.Exists(Path.Combine(control, "retired")));
                facts["retirement"] = "owned_job_close_terminated_only_verified_fake_host";
            }
            else
            {
                await Task.Delay(500); Assert.False(host.HasExited);
                facts["host_after_job_close"] = LifecycleObservation.Capture(host, "alive_after_no_kill_job_close");
                Set(control, "exit", "normal"); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(0, host.ExitCode);
                Assert.Equal("normal", File.ReadAllText(Path.Combine(control, "retired")));
                facts["retirement"] = "survived_job_close_then_normal_control_signal";
            }
            AssertNoAuthorityActions(control); facts["recording_posts"] = Count(control, "posts");
        }
        finally
        {
            Set(control, "exit", "normal");
            if (host != null && !host.HasExited)
            {
                facts["residual_before_control_cleanup"] = LifecycleObservation.Capture(host, "residual_control_cleanup");
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
            host?.Dispose();
        }
    });

    [Fact]
    public async Task StdinEofOnlyCausesAutonomousExitInExplicitSyntheticStdinPolicy()
        => await Diagnose("explicit-stdin-eof-policy-control", async facts =>
    {
        var data = fixture.NewPath("pipe-data"); Fixture.ExistingData(data); var control = fixture.NewPath("pipe-control"); Directory.CreateDirectory(control);
        var start = new ProcessStartInfo(App) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--api-fixture", "--data-dir", data, "--control", control, "--signal", Path.Combine(control, "exit"),
            "--lifecycle-observe", "--lifecycle-stdin-sensitive" }) start.ArgumentList.Add(arg);
        using var host = Process.Start(start)!; _ = host.Handle;
        var stdout = host.StandardOutput.ReadToEndAsync(); var stderr = host.StandardError.ReadToEndAsync();
        try
        {
            await Fixture.Eventually(() => File.Exists(Path.Combine(control, "ready")));
            facts["host_before_input_eof"] = LifecycleObservation.Capture(host, "before_input_eof", Environment.ProcessId);
            facts["host_self"] = Read<ProcessObservation>(Path.Combine(control, "app-start.json"));
            Assert.False(host.HasExited); host.StandardInput.Close();
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); Assert.Equal(0, host.ExitCode);
            Assert.True(File.Exists(Path.Combine(control, "stdin-eof-observed")));
            Assert.Equal("normal", File.ReadAllText(Path.Combine(control, "retired")));
            Assert.Equal("", await stdout); Assert.Equal("", await stderr); AssertNoAuthorityActions(control);
            facts["retirement"] = "synthetic_host_explicitly_observed_own_stdin_eof_not_product_policy";
        }
        finally { Set(control, "exit", "normal"); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
    });
}
