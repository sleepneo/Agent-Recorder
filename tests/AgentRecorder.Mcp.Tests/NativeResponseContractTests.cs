using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AgentRecorder.Mcp.Tests;

public sealed class NativeResponseContractTests
{
    private static JsonObject Status() => JsonNode.Parse("""
        {"recording_id":"rec_123456abcdef","status":"completed","mode":"video","output":
        {"path":"D:\\synthetic\\clip.mp4","bytes_written":68776,"duration_seconds":10,"container":"mp4","codec":"h264","width":1884,"height":792}}
        """)!.AsObject();
    private static JsonObject Output() => JsonNode.Parse("""
        {"recording_id":"rec_123456abcdef","status":"completed","mode":"video","output":
        {"path":"D:\\synthetic\\clip.mp4","exists":true,"size_bytes":68776,"duration_seconds":10,"container":"mp4","codec":"h264","width":1884,"height":792,"fps":30,"has_audio_stream":false,
        "probe_streams":[{"index":0,"codec_type":"video","codec_name":"h264","start_time_seconds":0,"duration_seconds":10}]}}
        """)!.AsObject();
    private static OperationResult Confirm(JsonObject status, JsonObject output)
    {
        var entry = new OperationEntry { SchemaVersion = 1, OperationId = "10532bf0-91ae-4795-817f-2cda8dbdf065",
            DurationSeconds = 10, Binding = null!, OwnerPid = 1, OwnerStartedTicks = 1,
            Status = "completed", ReasonCode = "recording_state_observed", RecordingId = "rec_123456abcdef", ConfirmationId = "confirm_123456abcdef" };
        return (OperationResult)typeof(OperationManager).GetMethod("ConfirmOutput", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [entry, JsonSerializer.SerializeToElement(status), JsonSerializer.SerializeToElement(output)])!;
    }

    [Fact]
    public void NativeStatusBytesWrittenAndFullOutputSizeBytesConfirmPublication()
    {
        var status = Status();
        Assert.False(status["output"]!.AsObject().ContainsKey("size_bytes"));
        var result = Confirm(status, Output());
        Assert.True(result.Ok); Assert.Equal("publication_confirmed", result.ReasonCode);
        Assert.Equal("confirm_123456abcdef", result.ConfirmationId);
        Assert.Equal(68776, result.OutputSizeBytes); Assert.Equal(10, result.OutputDurationSeconds);
    }

    public static IEnumerable<object[]> InvalidFields()
    {
        foreach (var side in new[] { "status", "output" })
        {
            var fields = side == "status"
                ? new[] { "recording_id", "status", "mode", "output", "output.path", "output.bytes_written" }
                : new[] { "recording_id", "status", "mode", "output", "output.path", "output.size_bytes", "output.exists",
                    "output.container", "output.codec", "output.width", "output.height", "output.fps", "output.duration_seconds",
                    "output.has_audio_stream", "output.probe_streams", "output.probe_streams.0", "output.probe_streams.0.codec_type", "output.probe_streams.0.codec_name" };
            foreach (var field in fields)
                foreach (var fault in new[] { "missing", "null", "wrong-type" }) yield return [side, field, fault];
        }
        foreach (var field in new[] { "output.size_bytes", "output.width", "output.height", "output.fps", "output.duration_seconds" })
            foreach (var fault in new[] { "negative", "zero" }) yield return ["output", field, fault];
        foreach (var fault in new[] { "negative", "zero", "mismatch" }) yield return ["status", "output.bytes_written", fault];
        yield return ["status", "output.path", "mismatch"];
        yield return ["output", "output.path", "mismatch"];
        yield return ["status", "recording_id", "mismatch"];
        yield return ["output", "recording_id", "mismatch"];
        yield return ["output", "output.duration_seconds", "mismatch"];
        yield return ["output", "output.has_audio_stream", "audio"];
        yield return ["output", "output.probe_streams", "audio"];
    }

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public void IncompleteOrInconsistentNativeEvidenceNeverConfirms(string side, string field, string fault)
    {
        var status = Status(); var output = Output(); JsonNode parent = side == "status" ? status : output;
        var segments = field.Split('.');
        foreach (var segment in segments[..^1]) parent = parent is JsonArray array ? array[int.Parse(segment)]! : parent[segment]!;
        var name = segments[^1]; var prior = parent is JsonArray items ? items[int.Parse(name)] : parent[name];
        JsonNode? replacement = fault switch
        {
            "null" => null,
            "wrong-type" => prior is JsonValue value && value.TryGetValue<string>(out _) ? JsonValue.Create(123) : JsonValue.Create("wrong-type"),
            "negative" => JsonValue.Create(-1), "zero" => JsonValue.Create(0),
            "mismatch" => name == "path" ? JsonValue.Create("D:\\synthetic\\different.mp4") : name == "recording_id" ? JsonValue.Create("rec_ffffffffffff") : JsonValue.Create(3),
            "audio" => name == "probe_streams" ? JsonNode.Parse("""[{"codec_type":"audio","codec_name":"aac"}]""") : JsonValue.Create(true),
            _ => null
        };
        if (parent is JsonArray arrayParent)
        {
            if (fault == "missing") arrayParent.RemoveAt(int.Parse(name)); else arrayParent[int.Parse(name)] = replacement;
        }
        else if (fault == "missing") parent.AsObject().Remove(name); else parent[name] = replacement;
        var result = Confirm(status, output);
        Assert.False(result.Ok); Assert.Equal("unknown", result.Status); Assert.Equal("publication_unconfirmed", result.ReasonCode);
        Assert.Null(result.OutputPath); Assert.Null(result.OutputSizeBytes); Assert.Null(result.OutputDurationSeconds);
        Assert.Equal("rec_123456abcdef", result.RecordingId);
    }

    [Fact]
    public void OldFixtureSizeBytesCannotSubstituteForNativeStatusBytesWritten()
    {
        var status = Status(); var initial = status["output"]!.AsObject();
        initial["size_bytes"] = initial["bytes_written"]!.DeepClone(); initial.Remove("bytes_written");
        Assert.Equal("publication_unconfirmed", Confirm(status, Output()).ReasonCode);
    }

    [Theory]
    [InlineData("user_requested")]
    [InlineData("floating_button")]
    [InlineData("tray_menu")]
    [InlineData("global_hotkey")]
    public void NativeUserStopReasonAllowsShortCompletedClipWithoutAnyLocalStopIntent(string reason)
    {
        var state = Status(); var output = Output(); state["stop_reason"] = reason;
        state["output"]!["duration_seconds"] = 3; output["output"]!["duration_seconds"] = 3;
        Assert.Equal("publication_confirmed", Confirm(state, output).ReasonCode);
    }

    [Theory]
    [InlineData("duration_reached")]
    [InlineData("backend_creation_failed")]
    [InlineData("unknown")]
    [InlineData("")]
    public void OtherReasonsDoNotRelaxNaturalDuration(string reason)
    {
        var state = Status(); var output = Output(); state["stop_reason"] = reason;
        output["output"]!["duration_seconds"] = 3;
        Assert.Equal("publication_unconfirmed", Confirm(state, output).ReasonCode);
    }
}
