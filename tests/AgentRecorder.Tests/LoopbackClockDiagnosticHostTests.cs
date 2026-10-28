using System.Text.Json;
using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class LoopbackClockDiagnosticHostTests
{
    internal static string DiagnosticLine()
    {
        var columns = "sequence,generation,input_sequence,sample_rate,block_align,native_frames,raw_device,raw_qpc_100ns,raw_flags,get_buffer_start_ticks,get_buffer_end_ticks,release_start_ticks,release_end_ticks,release_ok,chunk_offset_frames,frames,device,converted_packet_ticks,received_ticks,position_valid,data_discontinuity,outcome,written_bytes,skipped_bytes,zero_bytes,fault_kind,timeline_before,timeline_after,buffer_capacity_frames,poll_sleep_ms".Split(',');
        var timeline = "anchor_ticks,anchor_device,anchor_media_bytes,media_bytes,last_written_device_start,last_written_device_end,trusted_qpc_ticks,trusted_device,pending_qpc_ticks,pending_device,outliers,consecutive_outliers,confirmed_phase_rebases,absolute_phase_adjustment_ticks,phase_budget_ticks,jitter_tolerance_ticks,max_device_gap_frames".Split(',');
        object?[] row = { 1L, 1L, 1L, 48000, 8, 480, 960L, 1000L, 1, 100L, 101L, 102L, 103L, true,
            0, 480, 960L, 1000L, 110L, true, true, 4, 0L, 0L, 0L, 1, new long[17], new long[17], null, null };
        return "LoopbackClockDiagnosticsV1: " + JsonSerializer.Serialize(new
        {
            version = 1, timestamp_frequency = 10_000_000, qpc_units_per_second = 10_000_000,
            helper_mvid = Guid.Empty, naudio_core_mvid = Guid.Empty, naudio_wasapi_mvid = Guid.Empty,
            capacity = 64, total_observed = 1, omitted_packets = 0, fault_sequence = 1, fault_record_pinned = true,
            columns, timeline_columns = timeline, packets = new[] { row }, states = new[] { new long[] { 200, 1, 7, 3840 } }, states_omitted = 0
        });
    }

    [Fact]
    public void BoundedMetadataCanBeRecoveredFromMixedStderrWithoutLoggingUnrelatedText()
    {
        var input = "unrelated-helper-text\n" + DiagnosticLine() + "\n" + new string('v', 5000);
        Assert.True(LoopbackClockDiagnosticReader.TryRead(input, out var payload));
        Assert.Equal(1, payload.GetProperty("packets").GetArrayLength());
        Assert.DoesNotContain("unrelated-helper-text", payload.GetRawText());
        Assert.False(LoopbackClockDiagnosticReader.TryRead("embedded-not-a-line:" + DiagnosticLine(), out _));
    }
}
