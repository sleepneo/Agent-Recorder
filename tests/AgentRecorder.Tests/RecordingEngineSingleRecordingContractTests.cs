using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using AgentRecorder.Core;
using AgentRecorder.Logging;
using AgentRecorder.Infrastructure;
using ApiException = AgentRecorder.Infrastructure.ApiException;

namespace AgentRecorder.Tests;

/// <summary>
/// Contract tests for RecordingEngine single-recording enforcement.
///
/// These tests prove that the 409 RECORDING_ALREADY_RUNNING check in
/// CreateRecording (line 40-42) is enforced for all active states and is
/// bypassed for non-active (historical) states.
///
/// These tests do NOT require a real desktop, real window, real FFmpeg,
/// or any human confirmation. They only exercise the engine's concurrency
/// guard logic via direct CreateRecording calls with minimal/invalid bodies.
///
/// Evidence classification: code_contract
/// </summary>
public class RecordingEngineSingleRecordingContractTests
{
    // A no-op tray that never triggers confirmation callbacks.
    private sealed class NoOpTrayContext : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;

        public void RequestConfirmation(object summary, Action<ConfirmationDecision> callback) { /* never called synchronously */ }
        public void RequestRegionSelection(int timeoutSeconds,
            Action<string, int, int, int, int, string, string> callback) { /* no-op */ }
        public void SetRecording(object rec) { }
        public void SetIdle(object rec) { }
        public void SetAllIdle() { }
        public void ShowError(string text) { }
    }

    private RecordingEngine MakeEngine()
    {
        // Use parameterless constructor
        var engine = new RecordingEngine(new AuditLogger());
        return engine;
    }

    // Recording.Id is auto-generated (no public setter).
    // Simply create the Recording and add it to _recs directly via reflection.
    // The auto-generated ID is fine for our tests — we only care about state.
    private static Recording MakeRecording(RecState state)
    {
        var rec = new Recording { State = state };
        return rec;
    }

    // Inject recordings directly into _recs via reflection
    private static void InjectRecs(RecordingEngine engine, params Recording[] recs)
    {
        var recsDict = new ConcurrentDictionary<string, Recording>();
        foreach (var r in recs) recsDict[r.Id] = r;
        var field = typeof(RecordingEngine).GetField(
            "_recs",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(engine, recsDict);
    }

    // Minimal JSON body used for all tests.
    // The 409 check fires BEFORE source parsing, so this body is sufficient
    // to trigger the guard without needing a valid source.
    private static readonly JsonNode MinimalBody = JsonNode.Parse("{}")!;

    // -------------------------------------------------------------------------
    // Group 1: Active states MUST block new CreateRecording calls with 409
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(RecState.recording)]
    [InlineData(RecState.stopping)]
    [InlineData(RecState.pending_confirmation)]
    public void CreateRecording_WhenActiveStateExists_Throws409(RecState activeState)
    {
        // Arrange: engine has one active recording
        var engine = MakeEngine();
        InjectRecs(engine, MakeRecording(activeState));

        // Act & Assert: second call is rejected before source parsing
        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(MinimalBody, "test-agent", new NoOpTrayContext()));

        Assert.Equal(409, ex.Status);
        Assert.Equal("RECORDING_ALREADY_RUNNING", ex.Code);
        Assert.Contains("already running", ex.Message);
    }

    // -------------------------------------------------------------------------
    // Group 2: Only the first active recording blocks; a second active one
    //          also blocks (idempotent guard)
    // -------------------------------------------------------------------------

    [Fact]
    public void CreateRecording_WhenTwoActiveRecordingsExist_Throws409()
    {
        var engine = MakeEngine();
        InjectRecs(engine,
            MakeRecording(RecState.recording),
            MakeRecording(RecState.pending_confirmation));

        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(MinimalBody, "test-agent", new NoOpTrayContext()));

        Assert.Equal(409, ex.Status);
        Assert.Equal("RECORDING_ALREADY_RUNNING", ex.Code);
    }

    // -------------------------------------------------------------------------
    // Group 3: Historical states must NOT block new CreateRecording calls.
    //          The guard is bypassed, so we expect a different error from the
    //          downstream source parser (INVALID_ARGUMENT: source is required).
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(RecState.completed)]
    [InlineData(RecState.failed)]
    [InlineData(RecState.cancelled)]
    [InlineData(RecState.rejected)]
    [InlineData(RecState.expired)]
    [InlineData(RecState.paused)]
    [InlineData(RecState.created)]
    public void CreateRecording_WhenOnlyHistoricalStateExists_DoesNotThrow409(RecState histState)
    {
        // Arrange: engine has only historical recordings
        var engine = MakeEngine();
        InjectRecs(engine, MakeRecording(histState));

        // Act & Assert: guard is bypassed → source parsing fires → INVALID_ARGUMENT
        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(MinimalBody, "test-agent", new NoOpTrayContext()));

        // Must NOT be a 409
        Assert.NotEqual(409, ex.Status);
        // Must be the expected downstream error (source is required)
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    // -------------------------------------------------------------------------
    // Group 4: Empty _recs → no blocking (also goes to source parsing error)
    // -------------------------------------------------------------------------

    [Fact]
    public void CreateRecording_WhenNoRecordingsExist_DoesNotThrow409()
    {
        var engine = MakeEngine();
        // _recs starts empty by default

        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(MinimalBody, "test-agent", new NoOpTrayContext()));

        Assert.NotEqual(409, ex.Status);
        Assert.Equal(400, ex.Status);
        Assert.Equal("INVALID_ARGUMENT", ex.Code);
    }

    // -------------------------------------------------------------------------
    // Group 5: Mixed active + historical → active wins (409 fires)
    // -------------------------------------------------------------------------

    [Fact]
    public void CreateRecording_WhenActivePlusHistorical_Throws409()
    {
        var engine = MakeEngine();
        InjectRecs(engine,
            MakeRecording(RecState.recording),
            MakeRecording(RecState.completed));

        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(MinimalBody, "test-agent", new NoOpTrayContext()));

        Assert.Equal(409, ex.Status);
        Assert.Equal("RECORDING_ALREADY_RUNNING", ex.Code);
    }

    // -------------------------------------------------------------------------
    // Group 6: Error code and message are stable (contract)
    // -------------------------------------------------------------------------

    [Fact]
    public void CreateRecording_WhenActive_409ErrorCode_IsExactlyRECORDING_ALREADY_RUNNING()
    {
        var engine = MakeEngine();
        InjectRecs(engine, MakeRecording(RecState.recording));

        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(MinimalBody, "test-agent", new NoOpTrayContext()));

        Assert.Equal("RECORDING_ALREADY_RUNNING", ex.Code);
    }

    [Fact]
    public void CreateRecording_WhenActive_409Message_ContainsAlreadyRunning()
    {
        var engine = MakeEngine();
        InjectRecs(engine, MakeRecording(RecState.recording));

        var ex = Assert.Throws<ApiException>(() =>
            engine.CreateRecording(MinimalBody, "test-agent", new NoOpTrayContext()));

        Assert.Contains("already running", ex.Message);
    }
}
