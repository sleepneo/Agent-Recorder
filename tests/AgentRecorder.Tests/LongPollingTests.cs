using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

[Collection("HeadlessHostIntegration")]
public class LongPollingTests : IDisposable
{
    private readonly string _testDir;
    private readonly AuditLogger _audit;
    private readonly RecordingEngine _engine;
    private readonly FakeTrayContext _tray;

    public LongPollingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "AgentRecorderTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_testDir);
        Directory.CreateDirectory(Path.Combine(_testDir, "logs"));
        Directory.CreateDirectory(Path.Combine(_testDir, "config"));

        DataDirResolver.SetOverride(_testDir);
        ApiKeyAuth.InitializeForTesting(_testDir);
        _audit = new AuditLogger();
        _engine = new RecordingEngine(_audit);
        _tray = new FakeTrayContext();
        _engine.SetTray(_tray);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
        DataDirResolver.ClearOverride();
        ApiKeyAuth.ResetForTesting(null);
    }

    // =====================================================================
    // Confirmation wait tests
    // =====================================================================

    [Fact]
    public void GetConfirmationWait_StatusChanged_ReturnsImmediately()
    {
        var conf = new Confirmation { RecordingId = "test_rec_001" };
        conf.Status = "approved";
        _engine._confs[conf.Id] = conf;

        var result = _engine.GetConfirmationWait(conf.Id, "pending", 10000);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"approved\"", json);
        Assert.Contains("\"NextPollHintMs\":null", json);
        // wait is an object with requested_ms, elapsed_ms, timed_out
        Assert.Contains("\"RequestedMs\":10000", json);
        Assert.Contains("\"TimedOut\":false", json);
    }

    [Fact]
    public void GetConfirmationWait_StatusUnchanged_ReturnsAfterTimeout()
    {
        var conf = new Confirmation { RecordingId = "test_rec_002" };
        conf.Status = "pending";
        _engine._confs[conf.Id] = conf;

        var startTime = DateTime.UtcNow;
        var result = _engine.GetConfirmationWait(conf.Id, "pending", 500);
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        Assert.True(elapsed >= 400, $"Expected ~500ms wait, got {elapsed}ms");
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"pending\"", json);
        Assert.Contains("\"NextPollHintMs\":500", json);
        Assert.Contains("\"TimedOut\":true", json);
    }

    [Fact]
    public void GetConfirmationWait_StateChangeDuringWait_ReturnsEarly()
    {
        var conf = new Confirmation { RecordingId = "test_rec_003" };
        conf.Status = "pending";
        _engine._confs[conf.Id] = conf;

        Task.Run(async () =>
        {
            await Task.Delay(100);
            conf.Status = "approved";
            _engine.BumpStateVersion();
        });

        var startTime = DateTime.UtcNow;
        var result = _engine.GetConfirmationWait(conf.Id, "pending", 5000);
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        Assert.True(elapsed < 1000, $"Expected early return, got {elapsed}ms");
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"approved\"", json);
        Assert.Contains("\"NextPollHintMs\":null", json);
        Assert.Contains("\"TimedOut\":false", json);
    }

    [Fact]
    public void GetConfirmationWait_CaseInsensitive_MatchesUpperCase()
    {
        var conf = new Confirmation { RecordingId = "test_rec_004" };
        conf.Status = "PENDING";
        _engine._confs[conf.Id] = conf;

        // since_status lowercase should match uppercase status
        var result = _engine.GetConfirmationWait(conf.Id, "pending", 500);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"PENDING\"", json);
        // Should wait because statuses match (case-insensitive)
        Assert.Contains("\"TimedOut\":true", json);
        // next_poll_hint_ms should be 500 when status is PENDING (case-insensitive)
        Assert.Contains("\"NextPollHintMs\":500", json);
    }

    [Fact]
    public void GetConfirmationWait_CaseInsensitive_MatchesMixedCase()
    {
        var conf = new Confirmation { RecordingId = "test_rec_005" };
        conf.Status = "Pending";
        _engine._confs[conf.Id] = conf;

        // since_status uppercase should match mixed case status
        var result = _engine.GetConfirmationWait(conf.Id, "PENDING", 500);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"Pending\"", json);
        Assert.Contains("\"TimedOut\":true", json);
        // next_poll_hint_ms should be 500 when status is Pending (case-insensitive)
        Assert.Contains("\"NextPollHintMs\":500", json);
    }

    [Fact]
    public async Task GetConfirmationWait_UnrelatedStateChange_DoesNotPrematurelyReturn()
    {
        var conf1 = new Confirmation { RecordingId = "test_rec_a" };
        conf1.Status = "pending";
        _engine._confs[conf1.Id] = conf1;

        var conf2 = new Confirmation { RecordingId = "test_rec_b" };
        conf2.Status = "pending";
        _engine._confs[conf2.Id] = conf2;

        // Start waiting on conf1
        var startTime = DateTime.UtcNow;
        var waitTask = Task.Run(() => _engine.GetConfirmationWait(conf1.Id, "pending", 800));

        // After 100ms, change conf2 (unrelated) and bump state version
        await Task.Delay(100);
        conf2.Status = "approved";
        _engine.BumpStateVersion();

        var result = await waitTask;
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        // Should NOT return early; conf1 is still pending
        Assert.True(elapsed >= 700, $"Expected ~800ms wait (not prematurely returned), got {elapsed}ms");
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("\"Status\":\"pending\"", json);
        Assert.Contains("\"TimedOut\":true", json);
    }

    // =====================================================================
    // Recording wait tests
    // =====================================================================

    [Fact]
    public void GetStatusWait_StatusChanged_ReturnsImmediately()
    {
        var rec = CreateRecording(RecState.completed);
        _engine._recs[rec.Id] = rec;

        var result = _engine.GetStatusWait(rec.Id, "recording", 10000);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"completed\"", json);
        Assert.Contains("\"NextPollHintMs\":null", json);
        Assert.Contains("\"RequestedMs\":10000", json);
        Assert.Contains("\"TimedOut\":false", json);
    }

    [Fact]
    public void GetStatusWait_StatusUnchanged_ReturnsAfterTimeout()
    {
        var rec = CreateRecording(RecState.recording);
        _engine._recs[rec.Id] = rec;

        var startTime = DateTime.UtcNow;
        var result = _engine.GetStatusWait(rec.Id, "recording", 500);
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        Assert.True(elapsed >= 400, $"Expected ~500ms wait, got {elapsed}ms");
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"recording\"", json);
        Assert.Contains("\"NextPollHintMs\":1000", json);
        Assert.Contains("\"TimedOut\":true", json);
    }

    [Fact]
    public void GetStatusWait_StateChangeDuringWait_ReturnsEarly()
    {
        var rec = CreateRecording(RecState.recording);
        _engine._recs[rec.Id] = rec;

        Task.Run(async () =>
        {
            await Task.Delay(100);
            rec.State = RecState.completed;
            _engine.BumpStateVersion();
        });

        var startTime = DateTime.UtcNow;
        var result = _engine.GetStatusWait(rec.Id, "recording", 5000);
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        Assert.True(elapsed < 1000, $"Expected early return, got {elapsed}ms");
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"completed\"", json);
        Assert.Contains("\"NextPollHintMs\":null", json);
        Assert.Contains("\"TimedOut\":false", json);
    }

    [Fact]
    public void GetStatusWait_CaseInsensitive_MatchesUpperCase()
    {
        var rec = CreateRecording(RecState.recording);
        _engine._recs[rec.Id] = rec;

        // since_status uppercase should match lowercase state
        var result = _engine.GetStatusWait(rec.Id, "RECORDING", 500);
        var json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Status\":\"recording\"", json);
        Assert.Contains("\"TimedOut\":true", json);
    }

    [Fact]
    public void GetCreationRecordingWait_PendingConfirmation_TimesOutWithoutTransition()
    {
        var rec = CreateRecording(RecState.pending_confirmation);
        _engine._recs[rec.Id] = rec;

        var result = _engine.GetCreationRecordingWait(rec.Id, 120);
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;
        var recording = result.GetType().GetProperty("Recording")!.GetValue(result)!;

        Assert.Equal("pending_confirmation", result.GetType().GetProperty("Status")!.GetValue(result));
        Assert.Equal("pending_confirmation", recording.GetType().GetProperty("status")!.GetValue(recording));
        Assert.Equal("recording", wait.GetType().GetProperty("WaitFor")!.GetValue(wait));
        Assert.Equal(120, wait.GetType().GetProperty("RequestedMs")!.GetValue(wait));
        Assert.True((bool)wait.GetType().GetProperty("TimedOut")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("GoalReached")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("Terminal")!.GetValue(wait)!);
        Assert.Equal(RecState.pending_confirmation, rec.State);
    }

    [Fact]
    public async Task GetCreationRecordingWait_ReturnsOnlyAfterTrustedRecordingOrTerminal()
    {
        var rec = CreateRecording(RecState.preparing);
        _engine._recs[rec.Id] = rec;

        var waitTask = Task.Run(() => _engine.GetCreationRecordingWait(rec.Id, 5000));
        await Task.Delay(100);
        rec.State = RecState.countdown;
        _engine.BumpStateVersion();
        await Task.Delay(100);
        Assert.False(waitTask.IsCompleted);

        rec.State = RecState.recording;
        _engine.BumpStateVersion();
        var result = await waitTask;
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;

        Assert.Equal("recording", result.GetType().GetProperty("Status")!.GetValue(result));
        Assert.True((bool)wait.GetType().GetProperty("GoalReached")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("TimedOut")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("Terminal")!.GetValue(wait)!);
    }

    [Fact]
    public void GetCreationRecordingWait_AlreadyTerminal_ReturnsImmediately()
    {
        var rec = CreateRecording(RecState.rejected);
        _engine._recs[rec.Id] = rec;

        var result = _engine.GetCreationRecordingWait(rec.Id, 25000);
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;

        Assert.Equal("rejected", result.GetType().GetProperty("Status")!.GetValue(result));
        Assert.True((bool)wait.GetType().GetProperty("Terminal")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("GoalReached")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("TimedOut")!.GetValue(wait)!);
        Assert.True((int)wait.GetType().GetProperty("ElapsedMs")!.GetValue(wait)! < 1000);
    }

    [Fact]
    public void GetCreationRecordingWait_AlreadyRecording_ReturnsGoalImmediately()
    {
        var rec = CreateRecording(RecState.recording);
        _engine._recs[rec.Id] = rec;

        var result = _engine.GetCreationRecordingWait(rec.Id, 25000);
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;

        Assert.Equal("recording", result.GetType().GetProperty("Status")!.GetValue(result));
        Assert.True((bool)wait.GetType().GetProperty("GoalReached")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("Terminal")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("TimedOut")!.GetValue(wait)!);
    }

    [Fact]
    public void GetCreationRecordingWait_TimeoutBoundaryTransition_ReturnsOneCoherentGoalSnapshot()
    {
        var rec = CreateRecording(RecState.pending_confirmation);
        _engine._recs[rec.Id] = rec;

        long nowTicks = 0;
        _engine.CreationWaitTimestampProviderForTests = () => Volatile.Read(ref nowTicks);
        _engine.CreationWaitTimestampFrequencyForTests = 1000;
        _engine.BeforeCreationWaitBlocksForTests = _ => Volatile.Write(ref nowTicks, 5);
        _engine.BeforeCreationWaitSnapshotForTests = recording =>
        {
            lock (recording)
            {
                recording.State = RecState.recording;
                _engine.BumpStateVersion();
            }
            Volatile.Write(ref nowTicks, 5);
        };

        var result = _engine.GetCreationRecordingWait(rec.Id, 5);

        AssertCoherentCreationWait(result, "recording", goalReached: true);
    }

    [Fact]
    public async Task GetCreationRecordingWait_MilestoneAdvancesBeforeProjection_ContinuesToTerminal()
    {
        var rec = CreateRecording(RecState.preparing);
        _engine._recs[rec.Id] = rec;
        using var transitionObserved = new ManualResetEventSlim();
        int transitionCount = 0;
        _engine.BeforeCreationWaitSnapshotForTests = recording =>
        {
            if (Interlocked.Exchange(ref transitionCount, 1) != 0)
                return;

            lock (recording)
            {
                recording.State = RecState.stopping;
                _engine.BumpStateVersion();
            }
            transitionObserved.Set();
        };

        var waitTask = Task.Run(() => _engine.GetCreationRecordingWait(rec.Id, 5000));
        lock (rec)
        {
            rec.State = RecState.recording;
            _engine.BumpStateVersion();
        }

        Assert.True(transitionObserved.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(waitTask.IsCompleted);

        lock (rec)
        {
            rec.State = RecState.completed;
            rec.CompletedAtUtc = DateTime.UtcNow;
            rec.PublishFinalized();
            _engine.BumpStateVersion();
        }

        var result = await waitTask;
        AssertCoherentCreationWait(result, "completed", terminal: true);
    }

    [Fact]
    public void GetCreationRecordingWait_TransientRace_DoesNotRestartOriginalBudget()
    {
        var rec = CreateRecording(RecState.recording);
        _engine._recs[rec.Id] = rec;

        long nowTicks = 0;
        _engine.CreationWaitTimestampProviderForTests = () => Volatile.Read(ref nowTicks);
        _engine.CreationWaitTimestampFrequencyForTests = 1000;
        int snapshotCount = 0;
        _engine.BeforeCreationWaitSnapshotForTests = recording =>
        {
            if (Interlocked.Exchange(ref snapshotCount, 1) != 0)
                return;

            lock (recording)
            {
                recording.State = RecState.stopping;
                _engine.BumpStateVersion();
            }
            Volatile.Write(ref nowTicks, 1);
        };
        _engine.BeforeCreationWaitBlocksForTests = _ => Volatile.Write(ref nowTicks, 10);

        var result = _engine.GetCreationRecordingWait(rec.Id, 10);
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;

        AssertCoherentCreationWait(result, "stopping", timedOut: true);
        Assert.Equal(10, wait.GetType().GetProperty("ElapsedMs")!.GetValue(wait));
    }

    [Fact]
    public async Task GetCreationRecordingWait_ConfirmationExpiryWhileBlocked_ReturnsTerminalSnapshot()
    {
        var rec = CreateRecording(RecState.pending_confirmation);
        var conf = new Confirmation { RecordingId = rec.Id };
        rec.ConfirmationId = conf.Id;
        _engine._recs[rec.Id] = rec;
        _engine._confs[conf.Id] = conf;

        using var aboutToBlock = new ManualResetEventSlim();
        _engine.BeforeCreationWaitBlocksForTests = _ => aboutToBlock.Set();
        var waitTask = Task.Run(() => _engine.GetCreationRecordingWait(rec.Id, 5000));

        Assert.True(aboutToBlock.Wait(TimeSpan.FromSeconds(2)));
        _engine.TriggerConfirmationExpiryForTests(conf.Id);

        var result = await waitTask;
        AssertCoherentCreationWait(result, "expired", terminal: true);
        Assert.Equal("expired", conf.Status);
    }

    [Theory]
    [InlineData(RecState.expired)]
    [InlineData(RecState.cancelled)]
    [InlineData(RecState.failed)]
    [InlineData(RecState.completed)]
    public void GetCreationRecordingWait_AllTerminalStatesAreTerminal(RecState state)
    {
        var rec = CreateRecording(state);
        _engine._recs[rec.Id] = rec;

        var result = _engine.GetCreationRecordingWait(rec.Id, 25000);
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;

        Assert.Equal(state.ToString(), result.GetType().GetProperty("Status")!.GetValue(result));
        Assert.True((bool)wait.GetType().GetProperty("Terminal")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("GoalReached")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("TimedOut")!.GetValue(wait)!);
    }

    [Fact]
    public async Task GetCreationRecordingWait_UnrelatedPulse_DoesNotSatisfyWait()
    {
        var rec = CreateRecording(RecState.preparing);
        _engine._recs[rec.Id] = rec;
        var unrelated = CreateRecording(RecState.preparing);
        _engine._recs[unrelated.Id] = unrelated;

        var waitTask = Task.Run(() => _engine.GetCreationRecordingWait(rec.Id, 350));
        await Task.Delay(80);
        unrelated.State = RecState.recording;
        _engine.BumpStateVersion();

        var result = await waitTask;
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;
        Assert.Equal("preparing", result.GetType().GetProperty("Status")!.GetValue(result));
        Assert.True((bool)wait.GetType().GetProperty("TimedOut")!.GetValue(wait)!);
        Assert.False((bool)wait.GetType().GetProperty("GoalReached")!.GetValue(wait)!);
    }

    [Fact]
    public async Task GetStatusWait_UnrelatedStateChange_DoesNotPrematurelyReturn()
    {
        var rec1 = CreateRecording(RecState.recording);
        _engine._recs[rec1.Id] = rec1;

        var rec2 = CreateRecording(RecState.recording);
        _engine._recs[rec2.Id] = rec2;

        // Start waiting on rec1
        var startTime = DateTime.UtcNow;
        var waitTask = Task.Run(() => _engine.GetStatusWait(rec1.Id, "recording", 800));

        // After 100ms, change rec2 (unrelated) and bump state version
        await Task.Delay(100);
        rec2.State = RecState.completed;
        _engine.BumpStateVersion();

        var result = await waitTask;
        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

        // Should NOT return early; rec1 is still recording
        Assert.True(elapsed >= 700, $"Expected ~800ms wait (not prematurely returned), got {elapsed}ms");
        var json = JsonSerializer.Serialize(result);
        Assert.Contains("\"Status\":\"recording\"", json);
        Assert.Contains("\"TimedOut\":true", json);
    }

    // =====================================================================
    // wait_ms clamp tests
    // =====================================================================

    [Fact]
    public void ParseWaitMs_ClampsTo25000()
    {
        // Use reflection to test the private ParseWaitMs method on ApiServer
        var method = typeof(AgentRecorder.Api.ApiServer).GetMethod("ParseWaitMs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        Assert.Equal(0, method.Invoke(null, new object?[] { null }));
        Assert.Equal(0, method.Invoke(null, new object?[] { "" }));
        Assert.Equal(0, method.Invoke(null, new object?[] { "0" }));
        Assert.Equal(0, method.Invoke(null, new object?[] { "-1" }));
        Assert.Equal(0, method.Invoke(null, new object?[] { "abc" }));
        Assert.Equal(1000, method.Invoke(null, new object?[] { "1000" }));
        Assert.Equal(25000, method.Invoke(null, new object?[] { "25000" }));
        Assert.Equal(25000, method.Invoke(null, new object?[] { "999999" }));
    }

    [Fact]
    public void ParseCreationWaitMs_RequiresRecordingAndUsesBoundedIntegerContract()
    {
        var method = typeof(AgentRecorder.Api.ApiServer).GetMethod("ParseCreationWaitMs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        object? Invoke(params (string Key, string Value)[] pairs)
        {
            var query = new System.Collections.Generic.Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
                query[pair.Key] = pair.Value;
            return method!.Invoke(null, new object?[] { query });
        }

        Assert.Equal(25000, Invoke(("wait_for", "recording")));
        Assert.Equal(1, Invoke(("wait_for", "recording"), ("wait_ms", "1")));
        Assert.Equal(25000, Invoke(("wait_for", "recording"), ("wait_ms", "25000")));

        foreach (var pairs in new[]
        {
            new[] { ("wait_for", "other") },
            new[] { ("wait_ms", "1") },
            new[] { ("wait_for", "recording"), ("wait_ms", "0") },
            new[] { ("wait_for", "recording"), ("wait_ms", "25001") },
            new[] { ("wait_for", "recording"), ("wait_ms", "abc") }
        })
        {
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => Invoke(pairs));
            var apiException = Assert.IsType<ApiException>(exception.InnerException);
            Assert.Equal(400, apiException.Status);
            Assert.Equal("INVALID_ARGUMENT", apiException.Code);
        }
    }

    // =====================================================================
    // API layer tests
    // =====================================================================

    [Fact]
    public async Task Api_GetConfirmation_WithWaitParams_InvokesWaitMethod()
    {
        using var server = new TestApiServer(_engine, _audit, _tray);
        server.Start();
        await Task.Delay(100);

        var conf = new Confirmation { RecordingId = "test_rec_api_001" };
        conf.Status = "approved";
        _engine._confs[conf.Id] = conf;

        var url = $"http://localhost:37891/api/v1/confirmations/{conf.Id}?wait_ms=1000&since_status=pending";
        var apiKey = ApiKeyAuth.CurrentApiKey;
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Agent-Recorder-Key", apiKey);
        var response = await new HttpClient().SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!["data"]!;

        Assert.Equal("approved", json["status"]!.GetValue<string>());
        Assert.NotNull(json["wait"]);
        Assert.Equal(1000, json["wait"]!["requested_ms"]!.GetValue<int>());
        Assert.False(json["wait"]!["timed_out"]!.GetValue<bool>());
        Assert.Null(json["next_poll_hint_ms"]);
    }

    [Fact]
    public async Task Api_GetRecording_WithWaitParams_InvokesWaitMethod()
    {
        using var server = new TestApiServer(_engine, _audit, _tray);
        server.Start();
        await Task.Delay(100);

        var rec = CreateRecording(RecState.completed);
        _engine._recs[rec.Id] = rec;

        var url = $"http://localhost:37891/api/v1/recordings/{rec.Id}?wait_ms=1000&since_status=recording";
        var apiKey = ApiKeyAuth.CurrentApiKey;
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Agent-Recorder-Key", apiKey);
        var response = await new HttpClient().SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!["data"]!;

        Assert.Equal("completed", json["status"]!.GetValue<string>());
        Assert.NotNull(json["wait"]);
        Assert.Equal(1000, json["wait"]!["requested_ms"]!.GetValue<int>());
        Assert.False(json["wait"]!["timed_out"]!.GetValue<bool>());
        Assert.Null(json["next_poll_hint_ms"]);
    }

    [Fact]
    public async Task Api_GetConfirmation_WaitParams_ReturnsWaitObjectOnTimeout()
    {
        using var server = new TestApiServer(_engine, _audit, _tray);
        server.Start();
        await Task.Delay(100);

        var conf = new Confirmation { RecordingId = "test_rec_api_002" };
        conf.Status = "pending";
        _engine._confs[conf.Id] = conf;

        var url = $"http://localhost:37891/api/v1/confirmations/{conf.Id}?wait_ms=200&since_status=pending";
        var apiKey = ApiKeyAuth.CurrentApiKey;
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Agent-Recorder-Key", apiKey);
        var response = await new HttpClient().SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!["data"]!;

        Assert.Equal("pending", json["status"]!.GetValue<string>());
        Assert.NotNull(json["wait"]);
        Assert.Equal(200, json["wait"]!["requested_ms"]!.GetValue<int>());
        Assert.True(json["wait"]!["timed_out"]!.GetValue<bool>());
        Assert.Equal(500, json["next_poll_hint_ms"]!.GetValue<int>());
    }

    [Fact]
    public async Task Api_GetRecording_WaitParams_ReturnsWaitObjectOnTimeout()
    {
        using var server = new TestApiServer(_engine, _audit, _tray);
        server.Start();
        await Task.Delay(100);

        var rec = CreateRecording(RecState.recording);
        _engine._recs[rec.Id] = rec;

        var url = $"http://localhost:37891/api/v1/recordings/{rec.Id}?wait_ms=200&since_status=recording";
        var apiKey = ApiKeyAuth.CurrentApiKey;
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Agent-Recorder-Key", apiKey);
        var response = await new HttpClient().SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!["data"]!;

        Assert.Equal("recording", json["status"]!.GetValue<string>());
        Assert.NotNull(json["wait"]);
        Assert.Equal(200, json["wait"]!["requested_ms"]!.GetValue<int>());
        Assert.True(json["wait"]!["timed_out"]!.GetValue<bool>());
        Assert.Equal(1000, json["next_poll_hint_ms"]!.GetValue<int>());
    }

    private static void AssertCoherentCreationWait(
        object result,
        string expectedStatus,
        bool goalReached = false,
        bool terminal = false,
        bool timedOut = false)
    {
        var status = (string)result.GetType().GetProperty("Status")!.GetValue(result)!;
        var recording = result.GetType().GetProperty("Recording")!.GetValue(result)!;
        var recordingStatus = (string)recording.GetType().GetProperty("status")!.GetValue(recording)!;
        var wait = result.GetType().GetProperty("Wait")!.GetValue(result)!;
        var flags = new[]
        {
            (bool)wait.GetType().GetProperty("GoalReached")!.GetValue(wait)!,
            (bool)wait.GetType().GetProperty("Terminal")!.GetValue(wait)!,
            (bool)wait.GetType().GetProperty("TimedOut")!.GetValue(wait)!
        };

        Assert.Equal(expectedStatus, status);
        Assert.Equal(status, recordingStatus);
        Assert.Equal(1, flags.Count(flag => flag));
        Assert.Equal(goalReached, flags[0]);
        Assert.Equal(terminal, flags[1]);
        Assert.Equal(timedOut, flags[2]);
    }

    private Recording CreateRecording(RecState state)
    {
        var rec = new Recording();
        rec.State = state;
        rec.SourceType = "display";
        rec.SourceTitle = "Test Display";
        rec.BackendType = "ffmpeg";
        rec.StartedAtUtc = DateTime.UtcNow;
        if (state == RecState.completed)
            rec.CompletedAtUtc = DateTime.UtcNow;
        return rec;
    }
}

internal class FakeTrayContext : ITrayContext
{
    public string HostMode => "headless";
    public bool SupportsRegionSelectionUi => false;
    public void SetRecording(RecordingUiPresentation rec) { }
    public void SetIdle(RecordingUiPresentation rec) { }
    public void SetAllIdle() { }
    public void ShowError(string msg) { }
    public void RequestConfirmation(RecordingConfirmationPresentation presentation, Action<ConfirmationDecision> callback) => callback(ConfirmationDecision.Approve());
    public void RequestRegionSelection(int timeoutSeconds, Action<string, int, int, int, int, string, string> callback) =>
        callback("selection_cancelled", 0, 0, 0, 0, "", "");
}

internal class TestApiServer : IDisposable
{
    private readonly AgentRecorder.Api.ApiServer _server;

    public TestApiServer(RecordingEngine engine, AuditLogger audit, ITrayContext tray)
    {
        _server = new AgentRecorder.Api.ApiServer(engine, audit, tray);
    }

    public void Start() => _server.Start();
    public void Stop() => _server.Stop();
    public void Dispose() => Stop();
}
