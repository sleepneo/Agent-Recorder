using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using AgentRecorder.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Mcp.Tests;

public sealed class FakeApi : IAsyncDisposable
{
    private readonly Process process;
    public string Data { get; }
    public string Control { get; }
    private readonly string signal;
    private readonly Task<string> stdout, stderr;
    public int Pid => process.Id;
    public bool HasExited => process.HasExited;
    private FakeApi(Process process, string data, string control, string signal)
    { this.process = process; Data = data; Control = control; this.signal = signal; stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync(); }
    public static async Task<FakeApi> Start(Fixture fixture, bool observeLifecycle = false)
    {
        var data = fixture.NewPath("api-data"); Fixture.ExistingData(data);
        var control = fixture.NewPath("controls"); Directory.CreateDirectory(control);
        var signal = Path.Combine(control, "exit");
        var start = new ProcessStartInfo(Path.Combine(fixture.Package, "AgentRecorder.App", "AgentRecorder.App.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            ArgumentList = { "--api-fixture", "--data-dir", data, "--control", control, "--signal", signal }
        };
        if (observeLifecycle) start.ArgumentList.Add("--lifecycle-observe");
        var process = Process.Start(start)!;
        process.StandardInput.Close();
        var api = new FakeApi(process, data, control, signal);
        await Fixture.Eventually(() => File.Exists(Path.Combine(control, "ready")));
        return api;
    }
    public void Set(string key, string value = "yes") => File.WriteAllText(Path.Combine(Control, key), value);
    public int Posts => File.Exists(Path.Combine(Control, "posts")) ? File.ReadAllLines(Path.Combine(Control, "posts")).Length : 0;
    public int Authenticated => File.Exists(Path.Combine(Control, "authenticated")) ? File.ReadAllLines(Path.Combine(Control, "authenticated")).Length : 0;
    public string[] Routes => File.Exists(Path.Combine(Control, "routes")) ? File.ReadAllLines(Path.Combine(Control, "routes")) : [];
    public async ValueTask DisposeAsync()
    {
        File.WriteAllText(signal, "normal");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", await stdout); Assert.Equal("", await stderr);
        Assert.Equal("normal", File.ReadAllText(Path.Combine(Control, "retired")));
        process.Dispose();
    }
}

public sealed class RecordingOperationTests(Fixture fixture, ITestOutputHelper output) : IClassFixture<Fixture>
{
    private const string RecordingId = "rec_123456abcdef";
    private static string Key() => Guid.NewGuid().ToString("D");
    private static Dictionary<string, object?> Request(string id, int duration = 10) => new() { ["operation_id"] = id, ["duration_seconds"] = duration };
    private static Dictionary<string, object?> Query(string id) => new() { ["operation_id"] = id };
    private static string Status(JsonElement result) => result.GetProperty("status").GetString()!;
    private static string Reason(JsonElement result) => result.GetProperty("reason_code").GetString()!;
    private async Task Bind(Session session, FakeApi api)
    {
        fixture.Mode("reuse");
        var result = await session.Call("recorder_binding_set", new() { ["package_root"] = fixture.Package, ["data_dir"] = api.Data });
        Assert.Equal("binding_saved", Reason(result));
    }
    private static async Task<JsonElement> Until(Session session, string id, Func<JsonElement, bool> predicate, int timeout = 10000)
    {
        var timer = Stopwatch.StartNew(); JsonElement value;
        do
        {
            value = await session.Call("recorder_operation_get", Query(id));
            if (predicate(value)) return value;
            await Task.Delay(60);
        } while (timer.ElapsedMilliseconds < timeout);
        Assert.Fail(value.GetRawText()); return value;
    }

    [Fact]
    public async Task TimelyAdmissionQueryableSelectionApprovalAndVerifiedOutput()
    {
        output.WriteLine("Caller interactive desktop: " + InteractiveDesktopRuntime.ObserveCurrent().IsOnInteractiveDesktop);
        await using var api = await FakeApi.Start(fixture);
        var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        var timer = Stopwatch.StartNew(); var accepted = await session.Call("recorder_record_region", Request(id));
        Assert.Equal("accepted", Status(accepted)); Assert.True(timer.ElapsedMilliseconds < 2000);
        await Fixture.Eventually(() => api.Posts == 1, 10000);
        Assert.Equal("submitting", Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.False(File.Exists(Path.Combine(api.Control, "capture-count")));
        api.Set("selected");
        var operations = new OperationStore(new BindingStore(binding));
        await Fixture.Eventually(() => operations.Read(id)?.RecordingId != null);
        Assert.Equal("waiting_for_local_confirmation", operations.Read(id)!.Status);
        Assert.Equal("confirm_123456abcdef", operations.Read(id)!.ConfirmationId);
        var waiting = await Until(session, id, result => Status(result) == "waiting_for_local_confirmation");
        Assert.Equal(RecordingId, waiting.GetProperty("recording_id").GetString());
        Assert.Equal("confirm_123456abcdef", waiting.GetProperty("confirmation_id").GetString());
        var mapped = new OperationStore(new BindingStore(binding)).Read(id)!;
        Assert.Equal("waiting_for_local_confirmation", mapped.Status);
        Assert.Equal("confirm_123456abcdef", mapped.ConfirmationId);
        Assert.False(File.Exists(Path.Combine(api.Control, "capture-count")));
        api.Set("approved");
        foreach (var phase in new[] { "preparing", "countdown", "recording", "finalizing", "completed" })
        {
            api.Set("state", phase);
            var result = await session.Call("recorder_operation_get", Query(id));
            Assert.Equal(phase, Status(result));
            if (phase == "completed")
            {
                Assert.Equal("publication_confirmed", Reason(result));
                Assert.Equal(Path.Combine(api.Data, "synthetic.mp4"), result.GetProperty("output_path").GetString());
                Assert.Equal(4096, result.GetProperty("output_size_bytes").GetInt64());
            }
        }
        using var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(api.Control, "request.json")));
        Assert.Equal("selected_region", request.RootElement.GetProperty("target").GetProperty("type").GetString());
        Assert.Equal(120, request.RootElement.GetProperty("target").GetProperty("selection_timeout_seconds").GetInt32());
        Assert.Equal(3, request.RootElement.GetProperty("countdown_seconds").GetInt32());
        Assert.False(request.RootElement.GetProperty("audio").GetProperty("microphone").GetProperty("enabled").GetBoolean());
        Assert.False(request.RootElement.GetProperty("audio").GetProperty("system_audio").GetProperty("enabled").GetBoolean());
        Assert.Single(api.Routes, route => route.StartsWith("POST"));
        Assert.DoesNotContain(api.Routes, route => route.Contains("stop") || route.Contains("confirmations/") || route.Contains("history"));
        Assert.DoesNotContain("SECRET-fixture-key", File.ReadAllText(Path.Combine(binding + ".operations", id + ".json")));
    }

    [Fact]
    public async Task SameKeyConcurrentAcrossProcessesReplayDisconnectAndConflictNeverRepost()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var first = await fixture.Connect(binding); await Bind(first, api);
        await using var second = await fixture.Connect(binding);
        var results = await Task.WhenAll(first.Call("recorder_record_region", Request(id)),
            first.Call("recorder_record_region", Query(id)), second.Call("recorder_record_region", Query(id)));
        Assert.All(results, result => Assert.Contains(Status(result), new[] { "accepted", "submitting" }));
        await Fixture.Eventually(() => api.Posts == 1, 10000);
        Assert.Equal("operation_conflict", Reason(await second.Call("recorder_record_region", Request(id, 11))));
        await first.Eof(); await second.Eof();
        await using var restarted = await fixture.Connect(binding);
        Assert.Equal("unknown", Status(await restarted.Call("recorder_operation_get", Query(id))));
        Assert.Equal("unknown", Status(await restarted.Call("recorder_record_region", Request(id))));
        Assert.Equal(1, api.Posts); Assert.False(api.HasExited);
        Assert.Equal("operation_in_progress", Reason(await restarted.Call("recorder_record_region", Request(Key()))));
    }

    [Fact]
    public async Task KnownNativeIdSurvivesMcpRestartAndQueriesOnlyThatId()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("selected");
        await using (var session = await fixture.Connect(binding))
        {
            await Bind(session, api); await session.Call("recorder_record_region", Request(id));
            await Until(session, id, result => result.GetProperty("recording_id").ValueKind == JsonValueKind.String);
        }
        await using var restarted = await fixture.Connect(binding);
        api.Set("approved"); api.Set("state", "completed");
        Assert.Equal("completed", Status(await restarted.Call("recorder_operation_get", Query(id))));
        Assert.Equal("completed", Status(await restarted.Call("recorder_record_region", Request(id))));
        Assert.Equal(1, api.Posts);
        Assert.All(api.Routes.Where(route => route.StartsWith("GET /api/v1/recordings/")), route => Assert.Contains(RecordingId, route));
        api.Set("query-mode", "wrong-id");
        var wrong = await restarted.Call("recorder_operation_get", Query(id));
        Assert.Equal("unknown", Status(wrong)); Assert.Equal(RecordingId, wrong.GetProperty("recording_id").GetString());
    }

    [Fact]
    public async Task LegacyKnownRecordingWithNullConfirmationCompletesUsingOnlyOriginalGets()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("selected");
        await using (var session = await fixture.Connect(binding))
        {
            await Bind(session, api); await session.Call("recorder_record_region", Request(id));
            await Until(session, id, result => result.GetProperty("confirmation_id").GetString() == "confirm_123456abcdef");
        }
        // Only this synthetic journal is rewritten to the schema-1 shape 0.2.0
        // left after dropping a native confirm_ ID. No real operation is touched.
        var store = new OperationStore(new BindingStore(binding));
        using (var gate = await store.LockAsync(CancellationToken.None))
            store.Write(store.Read(id)! with { ConfirmationId = null, Status = "unknown" });
        var posts = api.Posts; var routes = api.Routes.Length;
        api.Set("approved"); api.Set("state", "completed");
        await using var upgraded = await fixture.Connect(binding);
        var result = await upgraded.Call("recorder_operation_get", Query(id));
        Assert.True(result.GetProperty("ok").GetBoolean()); Assert.Equal("publication_confirmed", Reason(result));
        Assert.Equal(RecordingId, result.GetProperty("recording_id").GetString()); Assert.Null(result.GetProperty("confirmation_id").GetString());
        Assert.Equal(posts, api.Posts); Assert.Equal(1, posts);
        Assert.All(api.Routes.Skip(routes), route => Assert.StartsWith("GET ", route));
        Assert.All(api.Routes.Skip(routes).Where(route => route.Contains("/recordings/")), route => Assert.Contains(RecordingId, route));
        Assert.DoesNotContain(api.Routes, route => route.Contains("confirmations/") || route.Contains("history") || route.Contains("stop"));
    }

    [Fact]
    public async Task HistoricalSyntheticConfPrefixFailsClosedWithoutQueryOrSubmission()
    {
        await using var api = await FakeApi.Start(fixture);
        var binding = fixture.NewPath("binding.json"); var id = Key(); var store = new OperationStore(new BindingStore(binding));
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        using (var gate = await store.LockAsync(CancellationToken.None))
            store.Write(new OperationEntry { SchemaVersion = 1, OperationId = id, DurationSeconds = 10, Binding = new BindingStore(binding).Read()!,
                OwnerPid = Environment.ProcessId, OwnerStartedTicks = 1, Status = "unknown", ReasonCode = "synthetic_history",
                RecordingId = RecordingId, ConfirmationId = "conf_123456abcdef" });
        Assert.Equal("operation_record_corrupt", Reason(await session.Call("recorder_operation_get", Query(id))));
        Assert.Equal(0, api.Posts); Assert.Empty(api.Routes);
    }

    [Theory]
    [InlineData("selection_cancelled")]
    [InlineData("selection_timeout")]
    [InlineData("error")]
    [InlineData("drop")]
    [InlineData("redirect")]
    [InlineData("huge")]
    [InlineData("truncated")]
    public async Task SelectionAndTransportFailuresDoNotRetry(string mode)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        api.Set("post-mode", mode); api.Set("selected");
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        await session.Call("recorder_record_region", Request(id));
        var result = await Until(session, id, value => Status(value) is "no_recording" or "not_submitted" or "unknown");
        var expected = mode.StartsWith("selection_") ? "no_recording" : mode == "error" ? "not_submitted" : "unknown";
        if (Status(result) != expected)
        {
            var store = new OperationStore(new BindingStore(binding));
            OperationStore.ReadEvidence? sameRead = null; OperationEntry? current = null; string? readError = null;
            // This is a NEW diagnostic observation, not the bytes read by the
            // request that produced result. Its own rejected snapshot is exact.
            try { current = store.ReadObserved(id, failure => sameRead ??= failure); }
            catch (Exception error) { readError = error is ContractException contract ? contract.Code : error.GetType().Name; }
            var evidenceRoot = Path.Combine(Fixture.Repo, ".local-data", "task434R", "selection-failure-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = OperationReadFailureEvidence.Save(evidenceRoot, id, sameRead, new
                {
                    mode, expected, actual_response = result, mcp = session.ObserveReadFailureOwner(),
                    create_posts = api.Posts, stop_posts = UpgradeCompatibilityScenarios.StopPosts(api),
                    followup_observation_only = true, read_evidence_scope = "failure_followup_read_not_original_request",
                    supplemental_current_entry = current, supplemental_read_error = readError
                });
                output.WriteLine("First selection mismatch evidence: " + path);
            }
            catch (Exception error) { output.WriteLine("Evidence persistence failed: " + error.GetType().Name); }
            output.WriteLine("First mismatched response: " + result.GetRawText());
        }
        Assert.Equal(expected, Status(result));
        await session.Call("recorder_record_region", Request(id));
        Assert.True(api.Posts == 1, result.GetRawText()); Assert.False(api.HasExited);
    }

    [Theory]
    [InlineData("wrong-instance")]
    [InlineData("wrong-pid")]
    [InlineData("wrong-version")]
    [InlineData("redirect")]
    public async Task UnprovenCapabilitiesFailBeforeKeyOrSubmission(string mode)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("caps-mode", mode);
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        await session.Call("recorder_record_region", Request(id));
        var result = await Until(session, id, value => Status(value) == "unknown");
        output.WriteLine("Fail closed: " + Reason(result));
        Assert.Equal(0, api.Posts); Assert.Equal(0, api.Authenticated);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("audio")]
    [InlineData("bad-duration")]
    public async Task CompletedWithoutValidSilentOutputRemainsUnknown(string mode)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("selected"); api.Set("output-mode", mode);
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        await session.Call("recorder_record_region", Request(id)); await Until(session, id, value => value.GetProperty("recording_id").ValueKind == JsonValueKind.String);
        api.Set("approved"); api.Set("state", "completed");
        var result = await session.Call("recorder_operation_get", Query(id));
        Assert.Equal("unknown", Status(result)); Assert.Null(result.GetProperty("output_path").GetString());
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task InvalidArgumentsAndUnknownLookupHaveNoWritesOrStartup()
    {
        var binding = fixture.NewPath("missing/binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding);
        foreach (var args in new Dictionary<string, object?>[] { new(), new() { ["operation_id"] = "REC" },
            new() { ["operation_id"] = id, ["duration_seconds"] = null }, new() { ["operation_id"] = id, ["duration_seconds"] = 0 },
            new() { ["operation_id"] = id, ["duration_seconds"] = 31 }, new() { ["operation_id"] = id, ["audio"] = false } })
            Assert.Equal("invalid_arguments", Reason(await session.Call("recorder_record_region", args)));
        Assert.Equal("not_found", Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.False(Directory.Exists(Path.GetDirectoryName(binding)));
    }

    [Fact]
    public async Task SelectionWaitIsNotTheShortReadDeadlineAndRemainsQueryable()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        await session.Call("recorder_record_region", Request(id)); await Fixture.Eventually(() => api.Posts == 1, 10000);
        await Task.Delay(5500);
        Assert.Equal("submitting", Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.Equal(1, api.Posts); Assert.False(File.Exists(Path.Combine(api.Control, "capture-count")));
        api.Set("selected");
        await Until(session, id, result => result.GetProperty("recording_id").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task QueryTimeoutRetainsNativeIdAndDoesNotStopHost()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("selected");
        await using var session = await fixture.Connect(binding); await Bind(session, api); await session.Call("recorder_record_region", Request(id));
        await Until(session, id, result => result.GetProperty("recording_id").ValueKind == JsonValueKind.String);
        api.Set("query-mode", "timeout"); var timer = Stopwatch.StartNew();
        var unknown = await session.Call("recorder_operation_get", Query(id));
        Assert.Equal("unknown", Status(unknown)); Assert.Equal(RecordingId, unknown.GetProperty("recording_id").GetString());
        Assert.InRange(timer.ElapsedMilliseconds, 4500, 9000); Assert.Equal(1, api.Posts); Assert.False(api.HasExited);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("expired")]
    public async Task NativeRefusalIsTerminalAndNeverResubmitted(string state)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("selected");
        await using var session = await fixture.Connect(binding); await Bind(session, api); await session.Call("recorder_record_region", Request(id));
        await Until(session, id, result => result.GetProperty("recording_id").ValueKind == JsonValueKind.String);
        api.Set("state", state);
        Assert.Equal(state, Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.Equal(state, Status(await session.Call("recorder_record_region", Request(id))));
        Assert.Equal(1, api.Posts); Assert.False(File.Exists(Path.Combine(api.Control, "capture-count")));
    }

    [Fact]
    public async Task ForeignSocketOwnerNeverReceivesKeyOrHttpRequest()
    {
        await using var api = await FakeApi.Start(fixture); await using var foreign = await FakeApi.Start(fixture);
        var path = Path.Combine(api.Data, "runtime", "ready.json"); var ready = JsonNode.Parse(File.ReadAllText(path))!;
        using var other = JsonDocument.Parse(File.ReadAllText(Path.Combine(foreign.Data, "runtime", "ready.json")));
        ready["port"] = other.RootElement.GetProperty("port").GetInt32(); File.WriteAllText(path, ready.ToJsonString());
        var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Bind(session, api); await session.Call("recorder_record_region", Request(id));
        await Until(session, id, result => Status(result) == "unknown");
        Assert.Empty(foreign.Routes); Assert.Equal(0, foreign.Authenticated); Assert.Equal(0, foreign.Posts); Assert.Equal(0, api.Posts);
    }

    [Fact]
    public async Task ReplacedDatabaseRejectsBeforeHostOrHttpAndRebindConflictsWithOldOperation()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json");
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        var db = Path.Combine(api.Data, "state", "agent-recorder.db"); File.Move(db, db + ".original"); Fixture.ExistingData(api.Data);
        Assert.Equal("initialized_data_replaced", Reason(await session.Call("recorder_record_region", Request(Key()))));
        Assert.Equal(0, api.Posts); Assert.Empty(api.Routes);
        await Bind(session, api); api.Set("post-mode", "selection_cancelled");
        var id = Key(); await session.Call("recorder_record_region", Request(id));
        await Until(session, id, result => Status(result) == "no_recording");
        var otherData = fixture.NewPath("other-data"); Fixture.ExistingData(otherData);
        await session.Call("recorder_binding_set", new() { ["package_root"] = fixture.Package, ["data_dir"] = otherData });
        Assert.Equal("operation_conflict", Reason(await session.Call("recorder_record_region", Request(id))));
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task JournalCorruptionLockAndWriteFailuresNeverReachProduct()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json");
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        var store = new OperationStore(new BindingStore(binding));
        using (var held = await store.LockAsync(CancellationToken.None))
        {
            var busy = await session.Call("recorder_record_region", Request(Key()));
            Assert.Equal("operation_store_busy", Reason(busy));
        }
        var bad = Key(); File.WriteAllText(store.PathFor(bad), "{\"schema_version\":99}");
        Assert.Equal("operation_record_corrupt", Reason(await session.Call("recorder_operation_get", Query(bad))));
        Assert.Equal("operation_record_corrupt", Reason(await session.Call("recorder_record_region", Request(Key()))));
        File.Delete(store.PathFor(bad));
        var failed = Key(); Directory.CreateDirectory(store.PathFor(failed));
        Assert.Equal("unknown", Status(await session.Call("recorder_record_region", Request(failed))));
        Assert.Equal(0, api.Posts); Assert.Empty(api.Routes);
        Assert.False(File.Exists(Path.Combine(api.Data, "runtime", "cli-arguments.jsonl")));
    }

    [Fact]
    public async Task PreCancelledSdkRequestCannotAdmitOrSubmit()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await session.Call("recorder_record_region", Request(id), cancellation.Token));
        Assert.Equal("not_found", Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.False(Directory.Exists(binding + ".operations")); Assert.Equal(0, api.Posts);
    }

    [Fact]
    public async Task MissingJournalAfterPostCannotBeReplayedAsNewOrAdmitAnotherKey()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        await session.Call("recorder_record_region", Request(id)); await Fixture.Eventually(() => api.Posts == 1, 10000);
        var journal = new OperationStore(new BindingStore(binding)); File.Delete(journal.PathFor(id));
        Assert.Equal("operation_record_missing", Reason(await session.Call("recorder_operation_get", Query(id))));
        Assert.Equal("operation_record_missing", Reason(await session.Call("recorder_record_region", Request(id))));
        Assert.Equal("operation_record_missing", Reason(await session.Call("recorder_record_region", Request(Key()))));
        Assert.Equal(1, api.Posts); Assert.False(api.HasExited);
    }

    [Fact]
    public async Task JournalChecksumAndRecordCapacityFailClosed()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json");
        await using var session = await fixture.Connect(binding); await Bind(session, api);
        var store = new OperationStore(new BindingStore(binding)); var originalBinding = new BindingStore(binding).Read()!;
        using (var gate = await store.LockAsync(CancellationToken.None))
        {
            for (int index = 0; index < OperationStore.MaximumRecords; index++)
                store.Write(new OperationEntry { SchemaVersion = 1, OperationId = Key(), DurationSeconds = 10, Binding = originalBinding,
                    OwnerPid = Environment.ProcessId, OwnerStartedTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    Status = "no_recording", ReasonCode = "selection_cancelled", Terminal = true });
        }
        Assert.Equal("operation_capacity_exceeded", Reason(await session.Call("recorder_record_region", Request(Key()))));
        var entry = store.Entries().First(); var path = store.PathFor(entry.OperationId);
        File.WriteAllText(path, File.ReadAllText(path).Replace("selection_cancelled", "selection_timeout", StringComparison.Ordinal));
        Assert.Equal("operation_record_corrupt", Reason(await session.Call("recorder_operation_get", Query(entry.OperationId))));
        Assert.Equal(0, api.Posts); Assert.Empty(api.Routes);
    }

    [Fact]
    public async Task ProxyEnvironmentCannotDivertAuthenticatedTraffic()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("selected");
        using var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        var proxyUrl = "http://127.0.0.1:" + ((IPEndPoint)proxy.LocalEndpoint).Port;
        await using var session = await Session.Start(fixture.Adapter, binding, fixture.NewPath("proxy-caller"), new Dictionary<string, string>
        { ["HTTP_PROXY"] = proxyUrl, ["HTTPS_PROXY"] = proxyUrl, ["ALL_PROXY"] = proxyUrl, ["NO_PROXY"] = "" });
        await Bind(session, api); await session.Call("recorder_record_region", Request(id));
        await Until(session, id, result => result.GetProperty("recording_id").ValueKind == JsonValueKind.String);
        Assert.Equal(1, api.Posts); Assert.False(proxy.Pending());
    }
}
