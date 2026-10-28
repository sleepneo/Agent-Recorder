using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public sealed class LoopbackPacketDiagnosticsTests
{
    private static readonly WaveFormat Format = new(48000, 32, 2);
    private static AudioPacketEventArgs Packet(AudioPacketObservation? native = null) =>
        new(Encoding.ASCII.GetBytes("private-pcm-secret-keep-out-of-logs"), 32, 4, 480, 1234567, 1234567, true,
            observation: native);

    [Fact]
    public void RingIsBoundedAndNeverRetainsPcmOrInventsMissingNativeFields()
    {
        var diagnostic = new LoopbackPacketDiagnostics();
        for (int n = 0; n < 1000; n++)
        {
            long seq = diagnostic.Observe(Packet(), 1, Format, 1_000_000 + n, null);
            diagnostic.Finish(seq, 3, 32, 0, 0, null);
        }
        var json = diagnostic.Export(10_000_000);
        Assert.DoesNotContain("private-pcm-secret", json);
        Assert.DoesNotContain(Convert.ToBase64String(Packet().Buffer), json);
        Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix + json, out var parsed));
        Assert.Equal(64, parsed.GetProperty("packets").GetArrayLength());
        Assert.Equal(936, parsed.GetProperty("omitted_packets").GetInt64());
        var row = parsed.GetProperty("packets")[0];
        Assert.Equal(937, row[0].GetInt64());
        foreach (int index in new[] { 2, 5, 6, 7, 8, 9, 10, 11, 12, 13 })
            Assert.Equal(JsonValueKind.Null, row[index].ValueKind);
        Assert.Equal(48000, row[3].GetInt32());
    }

    [Fact]
    public void NativeReleaseAndChunkEvidenceRemainSeparateAndAtomic()
    {
        var native = new AudioPacketObservation(2, 48000, 8, 480, 960, 100_000_000, 1 | 2, 100, 110);
        var original = Packet(native);
        original.DiagnosticGeneration = 4; original.DiagnosticReceivedTicks = 120;
        var clone = original.Clone();
        var diagnostic = new LoopbackPacketDiagnostics();
        long seq = diagnostic.Observe(clone, 4, Format, 120, new long[17]);
        diagnostic.Finish(seq, 4, 0, 0, 0, new long[17], 1);
        native.CompleteRelease(130, 140, true);
        Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix + diagnostic.Export(10_000_000), out var parsed));
        var row = parsed.GetProperty("packets")[0];
        Assert.Equal(2, row[2].GetInt64()); Assert.Equal(480, row[5].GetInt32());
        Assert.Equal(100_000_000, row[7].GetInt64()); Assert.Equal(3, row[8].GetInt32());
        Assert.Equal(130, row[11].GetInt64()); Assert.Equal(140, row[12].GetInt64()); Assert.True(row[13].GetBoolean());
        Assert.Equal(1, parsed.GetProperty("fault_sequence").GetInt64());
    }

    [Fact]
    public void ByteBudgetPinsFirstFaultKeepsNewestEvidenceAndBoundsPostFaultStates()
    {
        var diagnostic = new LoopbackPacketDiagnostics();
        long fault = 0;
        for (int n = 0; n < 1000; n++)
        {
            var native = new AudioPacketObservation(n + 1, 48000, 8, int.MaxValue, long.MaxValue,
                long.MaxValue, int.MaxValue, long.MaxValue, long.MaxValue);
            native.CompleteRelease(long.MaxValue, long.MaxValue, true);
            long seq = diagnostic.Observe(Packet(native), n / 100 + 1, Format, long.MaxValue,
                Enumerable.Repeat(long.MaxValue, 17).ToArray());
            diagnostic.Finish(seq, n == 0 ? 4 : 3, long.MaxValue, long.MaxValue, long.MaxValue,
                Enumerable.Repeat(long.MaxValue, 17).ToArray(), n == 0 ? 1 : 0);
            if (n == 0) fault = seq;
            diagnostic.State(6, n / 100 + 1);
        }
        var json = diagnostic.Export(10_000_000);
        Assert.InRange(json.Length, 1, LoopbackPacketDiagnostics.MaxJsonChars);
        Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix + json, out var parsed));
        var rows = parsed.GetProperty("packets");
        Assert.InRange(rows.GetArrayLength(), 2, 64);
        Assert.Equal(fault, rows[0][0].GetInt64());
        Assert.Equal(1000, rows[rows.GetArrayLength() - 1][0].GetInt64());
        Assert.Equal(8, parsed.GetProperty("states").GetArrayLength());
        Assert.Equal(992, parsed.GetProperty("states_omitted").GetInt64());
    }

    [Fact]
    public async Task ConcurrentSnapshotsAreConsistentWhilePacketsAndReleaseComplete()
    {
        var diagnostic = new LoopbackPacketDiagnostics();
        var producer = Task.Run(() =>
        {
            for (int n = 0; n < 2000; n++)
            {
                var native = new AudioPacketObservation(n + 1, 48000, 8, 480, n * 480, 1_000_000 + n, 0, n + 1, n + 2);
                long seq = diagnostic.Observe(Packet(native), 1, Format, n + 3, null);
                native.CompleteRelease(n + 4, n + 5, true);
                diagnostic.Finish(seq, 3, 32, 0, 0, null);
            }
        });
        for (int n = 0; n < 100; n++)
        {
            Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix + diagnostic.Export(10_000_000), out _));
            await Task.Yield();
        }
        await producer;
    }

    [Fact]
    public void BufferedPacketEvictedBeforeStartFlushStillRetainsItsFaultAndOriginalArrival()
    {
        var diagnostic = new LoopbackPacketDiagnostics();
        var first = Packet(); first.DiagnosticGeneration = 1; first.DiagnosticReceivedTicks = 123;
        first.DiagnosticSequence = diagnostic.Observe(first, 1, Format, 123, null);
        for (int n = 0; n < 255; n++) diagnostic.Observe(Packet(), 1, Format, 124 + n, null);
        var before = new long[17]; before[0] = 999;
        diagnostic.BeforeAppend(first, Format, before);
        diagnostic.Finish(first.DiagnosticSequence, 4, 0, 0, 0, before, 1);
        Assert.True(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix + diagnostic.Export(10_000_000), out var parsed));
        var fault = parsed.GetProperty("packets").EnumerateArray().Single(x => x[0].GetInt64() == 1);
        Assert.Equal(123, fault[18].GetInt64()); Assert.Equal(999, fault[26][0].GetInt64());
        Assert.Equal(1, parsed.GetProperty("fault_sequence").GetInt64());
    }

    [Theory]
    [InlineData(1_000_000L)] [InlineData(10_000_000L)] [InlineData(1_000_000_000L)]
    public void RawHundredNanosecondQpcIsConvertedUsingDeclaredFrequencyNotArrivalDelay(long frequency)
    {
        long raw = 100_000_000; // Ten seconds in WASAPI units.
        long early = AudioPacketPositionMath.ConvertQpcToStopwatchTicks(raw, frequency * 20, frequency);
        long late = AudioPacketPositionMath.ConvertQpcToStopwatchTicks(raw, frequency * 30, frequency);
        Assert.Equal(frequency * 10, early); Assert.Equal(early, late);
    }

    [Theory]
    [InlineData("extra_secret")] [InlineData("audio_bytes")] [InlineData("wrong_columns")]
    [InlineData("too_many_packets")] [InlineData("bad_guid")] [InlineData("fractional")]
    public void HostRejectsUnboundedOrNonMetadataPayloads(string fault)
    {
        var diagnostic = new LoopbackPacketDiagnostics();
        diagnostic.Observe(Packet(), 1, Format, 100, null);
        var root = JsonNode.Parse(diagnostic.Export(10_000_000))!.AsObject();
        switch (fault)
        {
            case "extra_secret": root["api_key"] = "must-never-log"; break;
            case "audio_bytes": root["packets"]![0]![6] = "private-pcm"; break;
            case "wrong_columns": root["columns"]![0] = "audio_bytes"; break;
            case "too_many_packets": while (root["packets"]!.AsArray().Count < 65) root["packets"]!.AsArray().Add(root["packets"]![0]!.DeepClone()); break;
            case "bad_guid": root["helper_mvid"] = "secret"; break;
            case "fractional": root["packets"]![0]![6] = 1.5; break;
        }
        Assert.False(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix + root.ToJsonString(), out _));
        Assert.False(LoopbackClockDiagnosticReader.TryRead(LoopbackPacketDiagnostics.Prefix + new string('x', 24001), out _));
    }
}
