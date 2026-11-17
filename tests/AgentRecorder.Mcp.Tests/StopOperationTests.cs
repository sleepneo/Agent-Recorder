using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Mcp.Tests;

public sealed class StopOperationTests(Fixture fixture, ITestOutputHelper output) : IClassFixture<Fixture>
{
    private const string Recording = "rec_123456abcdef", Other = "rec_abcdef123456";
    private static string Key() => Guid.NewGuid().ToString("D");
    private static Dictionary<string, object?> Query(string id) => new() { ["operation_id"] = id };
    private static Dictionary<string, object?> Stop(string id, string recording = Recording) => new() { ["operation_id"] = id, ["recording_id"] = recording };
    private static string Status(JsonElement result) => result.GetProperty("status").GetString()!;
    private static string Reason(JsonElement result) => result.GetProperty("reason_code").GetString()!;
    private static int Posts(FakeApi api) => File.Exists(Path.Combine(api.Control, "stop-posts")) ? File.ReadAllLines(Path.Combine(api.Control, "stop-posts")).Length : 0;
    private async Task Prepare(Session session, FakeApi api, string binding, string id, string state = "recording")
    {
        fixture.Mode("reuse"); api.Set("selected"); api.Set("approved");
        Assert.Equal("binding_saved", Reason(await session.Call("recorder_binding_set", new() { ["package_root"] = fixture.Package, ["data_dir"] = api.Data })));
        await session.Call("recorder_record_region", new() { ["operation_id"] = id, ["duration_seconds"] = 30 });
        var store = new OperationStore(new BindingStore(binding));
        OperationStore.ReadEvidence? firstReadFailure = null;
        var observed = new List<object>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> trace = (_, eventArgs) =>
        {
            var error = eventArgs.Exception;
            if (error.StackTrace?.Contains("AgentRecorder.Mcp.OperationStore.Read", StringComparison.Ordinal) == true)
                lock (observed) { if (observed.Count < 8) observed.Add(new { type = error.GetType().Name, hresult = error.HResult }); }
        };
        AppDomain.CurrentDomain.FirstChanceException += trace;
        try { await Fixture.Eventually(() => store.ReadObserved(id, read => firstReadFailure ??= read)?.RecordingId == Recording, 10000); }
        catch
        {
            // Persist before reopening the path; only this snapshot belongs to
            // the rejected read. Later path bytes remain supplemental evidence.
            var evidenceDirectory = Path.Combine(Fixture.Repo, ".local-data", "task434R", "read-failure-" + Guid.NewGuid().ToString("N"));
            try
            {
                var saved = OperationReadFailureEvidence.Save(evidenceDirectory, id, firstReadFailure,
                    new { process = session.ObserveReadFailureOwner(), binding });
                output.WriteLine("Same-read first failure: " + saved);
            }
            catch (Exception evidenceError) { output.WriteLine("Evidence persistence failed: " + evidenceError.GetType().Name); }
            long? length = null; string? hash = null;
            try { var bytes = File.ReadAllBytes(store.PathFor(id)); length = bytes.Length; hash = Convert.ToHexString(SHA256.HashData(bytes)); } catch { }
            output.WriteLine("Journal wait failure: " + JsonSerializer.Serialize(new { operation_id = id, path = store.PathFor(id), length, sha256 = hash, first_chance = observed }));
            throw; // Never retry/ignore a corrupt or uncertain operation record.
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= trace; }
        api.Set("state", state);
    }

    [Fact]
    public async Task ExactStopAcceptsOnlyMappedIdAndShortClipKeepsOtherRecordingUntouched()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id);
        Assert.Equal("operation_conflict", Reason(await session.Call("recorder_recording_stop", Stop(id, Other))));
        Assert.Equal(0, Posts(api));
        var stopped = await session.Call("recorder_recording_stop", Stop(id));
        Assert.Equal("accepted", Status(stopped)); Assert.Equal("stopping", stopped.GetProperty("product_status").GetString());
        Assert.False(stopped.TryGetProperty("output_path", out _));
        Assert.Equal("stopping", Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.Equal("recording", File.ReadAllText(Path.Combine(api.Control, "other-state")));
        Assert.Equal(new[] { Recording }, File.ReadAllLines(Path.Combine(api.Control, "stop-posts")));
        using var request = JsonDocument.Parse(File.ReadAllText(Path.Combine(api.Control, "stop-request.json")));
        Assert.Single(request.RootElement.EnumerateObject()); Assert.Equal("user_requested", request.RootElement.GetProperty("reason").GetString());
        api.Set("state", "completed"); api.Set("output-duration", "3");
        var output = await session.Call("recorder_operation_get", Query(id));
        Assert.Equal("publication_confirmed", Reason(output)); Assert.Equal(3, output.GetProperty("output_duration_seconds").GetDouble());
        Assert.Equal("user_requested", output.GetProperty("product_error_code").GetString());
        Assert.Equal("already_terminal", Status(await session.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal(1, Posts(api)); Assert.False(api.HasExited);
        Assert.DoesNotContain(api.Routes, route => route.Contains(Other) || route.Contains("confirmations/") || route.Contains("history") || route.Contains("shutdown"));
    }

    [Fact]
    public async Task ConcurrentStopAndStaleQueryCannotEraseIntentAndRestartNeverReposts()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var first = await fixture.Connect(binding); await Prepare(first, api, binding, id);
        await using var second = await fixture.Connect(binding);
        api.Set("query-pause", "once"); var staleRead = first.Call("recorder_operation_get", Query(id));
        await Fixture.Eventually(() => File.Exists(Path.Combine(api.Control, "query-observed")));
        api.Set("stop-mode", "hold"); var pending = first.Call("recorder_recording_stop", Stop(id));
        await Fixture.Eventually(() => Posts(api) == 1);
        var duplicates = await Task.WhenAll(first.Call("recorder_recording_stop", Stop(id)), second.Call("recorder_recording_stop", Stop(id)));
        Assert.All(duplicates, result => Assert.Equal("unknown", Status(result)));
        api.Set("query-release"); Assert.Equal("recording", Status(await staleRead));
        var operations = new OperationStore(new BindingStore(binding)); var stops = new StopStore(operations);
        Assert.Equal("intent", stops.Read(id)!.Status);
        var timer = Stopwatch.StartNew(); Assert.Equal("stopping", Status(await second.Call("recorder_operation_get", Query(id))));
        Assert.True(timer.ElapsedMilliseconds < 2000);
        api.Set("stop-release"); Assert.Equal("accepted", Status(await pending));
        await first.Eof(); await second.Eof();
        await using var restarted = await fixture.Connect(binding);
        Assert.Equal("accepted", Status(await restarted.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal("stopping", Status(await restarted.Call("recorder_operation_get", Query(id))));
        Assert.Equal(1, Posts(api)); Assert.Single(operations.Entries());
        Assert.DoesNotContain("SECRET-fixture-key", File.ReadAllText(stops.PathFor(id)));
    }

    [Fact]
    public async Task SimultaneousSameAndCrossProcessStopAdmissionSendsAtMostOnce()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var first = await fixture.Connect(binding); await Prepare(first, api, binding, id);
        await using var second = await fixture.Connect(binding); api.Set("stop-mode", "hold");
        var calls = new[] { first.Call("recorder_recording_stop", Stop(id)), first.Call("recorder_recording_stop", Stop(id)), second.Call("recorder_recording_stop", Stop(id)) };
        await Fixture.Eventually(() => Posts(api) == 1); api.Set("stop-release");
        var results = await Task.WhenAll(calls);
        Assert.Contains(results, result => Status(result) == "accepted");
        Assert.All(results, result => Assert.Contains(Status(result), new[] { "accepted", "unknown", "not_sent" }));
        Assert.Equal(1, Posts(api)); Assert.Equal("recording", File.ReadAllText(Path.Combine(api.Control, "other-state")));
    }

    [Fact]
    public async Task StrictArgumentsUnknownOperationsAndMissingNativeMappingNeverSubmit()
    {
        var binding = fixture.NewPath("missing/binding.json"); var id = Key();
        await using (var session = await fixture.Connect(binding))
        {
            foreach (var args in new Dictionary<string, object?>[] { new(), Query(id), new() { ["operation_id"] = null, ["recording_id"] = Recording },
                new() { ["operation_id"] = id, ["recording_id"] = null }, new() { ["operation_id"] = id, ["recording_id"] = 42 },
                new() { ["operation_id"] = id, ["recording_id"] = "https://localhost/stop" }, Stop(id, "REC_123456abcdef"), Stop(id, "rec_123"),
                new() { ["operation_id"] = id, ["recording_id"] = Recording, ["force"] = true },
                new() { ["operation_id"] = id, ["recording_id"] = Recording, ["reason"] = "user_requested" } })
                Assert.Equal("invalid_arguments", Reason(await session.Call("recorder_recording_stop", args)));
            Assert.Equal("operation_not_found", Reason(await session.Call("recorder_recording_stop", Stop(id))));
            Assert.False(Directory.Exists(Path.GetDirectoryName(binding)));
        }
        await using var api = await FakeApi.Start(fixture); binding = fixture.NewPath("binding.json");
        await using var configured = await fixture.Connect(binding); fixture.Mode("reuse");
        await configured.Call("recorder_binding_set", new() { ["package_root"] = fixture.Package, ["data_dir"] = api.Data });
        await configured.Call("recorder_record_region", new() { ["operation_id"] = id });
        await Fixture.Eventually(() => api.Posts == 1);
        Assert.Equal("native_mapping_unconfirmed", Reason(await configured.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal(0, Posts(api)); Assert.Equal("submitting", Status(await configured.Call("recorder_operation_get", Query(id))));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("binding")]
    [InlineData("database")]
    public async Task ChangedIdentityRejectsStopBeforeSideEffect(string change)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id);
        if (change == "host")
        {
            var path = Path.Combine(api.Data, "runtime", "ready.json"); var ready = JsonNode.Parse(File.ReadAllText(path))!;
            ready["instance_id"] = Key(); File.WriteAllText(path, ready.ToJsonString());
        }
        else if (change == "binding")
        {
            var otherData = fixture.NewPath("other-data"); Fixture.ExistingData(otherData);
            await session.Call("recorder_binding_set", new() { ["package_root"] = fixture.Package, ["data_dir"] = otherData });
        }
        else { var db = Path.Combine(api.Data, "state", "agent-recorder.db"); File.Move(db, db + ".original"); Fixture.ExistingData(api.Data); }
        var result = await session.Call("recorder_recording_stop", Stop(id));
        Assert.Equal("not_sent", Status(result)); Assert.Equal(0, Posts(api));
        Assert.Equal(Recording, result.GetProperty("recording_id").GetString()); Assert.False(api.HasExited);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("rejected")]
    [InlineData("expired")]
    public async Task AlreadyTerminalOnlyReadsAndDoesNotAttributeNaturalCompletionToStop(string state)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id, state);
        var result = await session.Call("recorder_recording_stop", Stop(id));
        Assert.Equal("already_terminal", Status(result)); Assert.Equal(state, result.GetProperty("product_status").GetString());
        Assert.Equal(0, Posts(api)); Assert.False(Directory.Exists(new StopStore(new OperationStore(new BindingStore(binding))).Root));
        var output = await session.Call("recorder_operation_get", Query(id));
        Assert.Equal(state == "completed", output.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("stopping")]
    [InlineData("finalizing")]
    public async Task AlreadySettlingRemainsPendingWithoutAnotherStopOrCompletionClaim(string state)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id, state);
        var result = await session.Call("recorder_recording_stop", Stop(id));
        Assert.Equal("not_sent", Status(result)); Assert.Equal("recording_already_settling", Reason(result));
        var read = await session.Call("recorder_operation_get", Query(id));
        Assert.Equal(state, Status(read)); Assert.Null(read.GetProperty("output_path").GetString()); Assert.Equal(0, Posts(api));
    }

    [Fact]
    public async Task PreflightReadTimeoutCannotSendStopAndHasShortBudget()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id); api.Set("query-mode", "timeout");
        var timer = Stopwatch.StartNew(); var result = await session.Call("recorder_recording_stop", Stop(id));
        Assert.Equal("not_sent", Status(result)); Assert.InRange(timer.ElapsedMilliseconds, 4500, 10000); Assert.Equal(0, Posts(api));
        Assert.False(Directory.Exists(new StopStore(new OperationStore(new BindingStore(binding))).Root)); Assert.False(api.HasExited);
    }

    [Fact]
    public async Task BoundedStopCapacityDoesNotPolluteOperationEnumerationOrSend()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id);
        var operations = new OperationStore(new BindingStore(binding)); var stops = new StopStore(operations); var entry = operations.Read(id)!;
        using (var gate = await operations.LockAsync(CancellationToken.None))
            for (int index = 0; index < OperationStore.MaximumRecords; index++)
                stops.WriteIntent(new(1, Key(), Recording, entry.Binding, entry.Host!, "intent", "stop_submission_intent"));
        Assert.Equal("stop_capacity_exceeded", Reason(await session.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal(0, Posts(api)); Assert.Single(operations.Entries());
    }

    [Theory]
    [InlineData("pending_confirmation")]
    [InlineData("preparing")]
    [InlineData("countdown")]
    public async Task PreCaptureStopUsesNativeCancellationWithoutApprovingOrInventingVideo(string state)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id, state);
        var result = await session.Call("recorder_recording_stop", Stop(id));
        Assert.Equal("accepted", Status(result)); Assert.Equal("cancelled", result.GetProperty("product_status").GetString());
        var query = await session.Call("recorder_operation_get", Query(id));
        Assert.Equal("cancelled", Status(query)); Assert.False(query.GetProperty("ok").GetBoolean()); Assert.Null(query.GetProperty("output_path").GetString());
        Assert.Equal(1, Posts(api)); Assert.DoesNotContain(api.Routes, route => route.Contains("confirmations/"));
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("truncated")]
    [InlineData("huge")]
    [InlineData("redirect")]
    [InlineData("timeout")]
    [InlineData("wrong-id")]
    [InlineData("error")]
    public async Task StopTransportUncertaintyNeverRepostsAcrossRestart(string mode)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("stop-mode", mode);
        await using (var session = await fixture.Connect(binding))
        {
            await Prepare(session, api, binding, id); var timer = Stopwatch.StartNew();
            var result = await session.Call("recorder_recording_stop", Stop(id));
            Assert.Equal(mode == "error" ? "rejected" : "unknown", Status(result)); Assert.True(timer.ElapsedMilliseconds < 10000);
            Assert.Equal(Recording, result.GetProperty("recording_id").GetString());
        }
        await using var restarted = await fixture.Connect(binding);
        Assert.Equal(mode == "error" ? "rejected" : "unknown", Status(await restarted.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal(1, Posts(api)); Assert.False(api.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrEofDuringStopOnlyEndsObservation(bool eof)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("stop-mode", "hold");
        await using (var session = await fixture.Connect(binding))
        {
            await Prepare(session, api, binding, id); using var cancelled = new CancellationTokenSource();
            var pending = session.Call("recorder_recording_stop", Stop(id), cancelled.Token);
            await Fixture.Eventually(() => Posts(api) == 1);
            if (eof) await session.Eof(); else cancelled.Cancel();
            try { await pending; } catch (Exception) { /* SDK cannot deliver a result after cancellation/EOF. */ }
        }
        await using var restarted = await fixture.Connect(binding);
        Assert.Equal("unknown", Status(await restarted.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal(1, Posts(api)); Assert.False(api.HasExited);
        Assert.DoesNotContain(api.Routes, route => route.Contains("shutdown") || route.Contains("confirmations/"));
    }

    [Fact]
    public async Task PreCancelledStopDoesNotCreateEvidenceOrSend()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Call("recorder_recording_stop", Stop(id), cancelled.Token));
        Assert.Equal(0, Posts(api)); Assert.False(Directory.Exists(new StopStore(new OperationStore(new BindingStore(binding))).Root));
    }

    [Fact]
    public async Task IntentWriteFailureLeavesTombstoneAndGateFailureNeverSends()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id);
        var operations = new OperationStore(new BindingStore(binding)); var stops = new StopStore(operations);
        using (var gate = await operations.LockAsync(CancellationToken.None))
        {
            Assert.Equal("operation_store_busy", Reason(await session.Call("recorder_recording_stop", Stop(id))));
            var entry = operations.Read(id)!; Directory.CreateDirectory(stops.PathFor(id));
            Assert.ThrowsAny<IOException>(() => stops.WriteIntent(new(1, id, Recording, entry.Binding, entry.Host!, "intent", "stop_submission_intent")));
            Directory.Delete(stops.PathFor(id)); // Only this synthetic empty collision directory.
        }
        Assert.Equal("stop_evidence_missing", Reason(await session.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal(0, Posts(api)); Assert.Equal("recording", Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.Equal("operation_in_progress", Reason(await session.Call("recorder_record_region", Query(Key()))));
    }

    [Fact]
    public async Task ReceiptWriteFailureAndMissingEvidenceNeverRepeatStop()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key(); api.Set("stop-mode", "hold");
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id);
        var stops = new StopStore(new OperationStore(new BindingStore(binding)));
        var pending = session.Call("recorder_recording_stop", Stop(id)); await Fixture.Eventually(() => Posts(api) == 1);
        using (var denyReplace = new FileStream(stops.PathFor(id), FileMode.Open, FileAccess.Read, FileShare.Read))
        { api.Set("stop-release"); Assert.Equal("unknown", Status(await pending)); }
        Assert.Equal("intent", stops.Read(id)!.Status);
        Assert.Equal("unknown", Status(await session.Call("recorder_recording_stop", Stop(id))));
        File.Delete(stops.PathFor(id));
        Assert.Equal("stop_evidence_missing", Reason(await session.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal(1, Posts(api)); Assert.Equal("stopping", Status(await session.Call("recorder_operation_get", Query(id))));
    }

    [Fact]
    public async Task FrozenSchemaOneChecksumNullConfirmationAndSidecarRemainIndependent()
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id);
        var operations = new OperationStore(new BindingStore(binding)); var e = operations.Read(id)!;
        // Explicit frozen 0.2.1 property set, not serialization of current OperationEntry.
        var legacy = new { e.SchemaVersion, e.OperationId, e.DurationSeconds, e.Binding, e.OwnerPid, e.OwnerStartedTicks,
            e.Status, e.ReasonCode, e.Host, e.RecordingId, ConfirmationId = (string?)null, e.ProductStatus, e.ProductErrorCode, e.Terminal };
        var checksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacy, Json.Options)));
        using (var gate = await operations.LockAsync(CancellationToken.None))
            File.WriteAllText(operations.PathFor(id), JsonSerializer.Serialize(new { Entry = legacy, Checksum = checksum }, Json.Options));
        Assert.Null(operations.Read(id)!.ConfirmationId);
        Assert.Equal("recording", Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.Equal("accepted", Status(await session.Call("recorder_recording_stop", Stop(id))));
        Assert.Single(operations.Entries()); Assert.Null(operations.Read(id)!.ConfirmationId);
        var stops = new StopStore(operations); File.WriteAllText(stops.PathFor(id), "{\"schema_version\":99}");
        Assert.Equal("stop_evidence_corrupt", Reason(await session.Call("recorder_recording_stop", Stop(id))));
        Assert.Equal("stopping", Status(await session.Call("recorder_operation_get", Query(id))));
        Assert.Equal(1, api.Posts); Assert.Equal(1, Posts(api));
    }

    [Theory]
    [InlineData("duration_reached", "completed", "", "3")]
    [InlineData("unknown_reason", "completed", "", "3")]
    [InlineData("user_requested", "failed", "", "3")]
    [InlineData("user_requested", "completed", "missing", "3")]
    [InlineData("user_requested", "completed", "audio", "3")]
    [InlineData("user_requested", "completed", "bad-duration", "3")]
    [InlineData("user_requested", "completed", "", "40")]
    public async Task StopIntentDoesNotRelaxNaturalDurationOrInvalidTerminalMedia(string reason, string state, string outputMode, string duration)
    {
        await using var api = await FakeApi.Start(fixture); var binding = fixture.NewPath("binding.json"); var id = Key();
        await using var session = await fixture.Connect(binding); await Prepare(session, api, binding, id);
        Assert.Equal("accepted", Status(await session.Call("recorder_recording_stop", Stop(id))));
        api.Set("stop-reason", reason); api.Set("state", state); api.Set("output-mode", outputMode); api.Set("output-duration", duration);
        var result = await session.Call("recorder_operation_get", Query(id));
        Assert.False(result.GetProperty("ok").GetBoolean()); Assert.Null(result.GetProperty("output_path").GetString());
        Assert.Equal(1, Posts(api));
    }
}
