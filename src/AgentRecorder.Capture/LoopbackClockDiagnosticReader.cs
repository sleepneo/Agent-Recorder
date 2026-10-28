using System.Text.Json;

namespace AgentRecorder.Capture;

/// <summary>Reads only the bounded metadata schema emitted by the loopback helper.</summary>
public static class LoopbackClockDiagnosticReader
{
    private const string Prefix = "LoopbackClockDiagnosticsV1: ";
    public const int MaxJsonChars = 24000;
    private static readonly string[] Columns =
    {
        "sequence", "generation", "input_sequence", "sample_rate", "block_align",
        "native_frames", "raw_device", "raw_qpc_100ns", "raw_flags",
        "get_buffer_start_ticks", "get_buffer_end_ticks", "release_start_ticks", "release_end_ticks", "release_ok",
        "chunk_offset_frames", "frames", "device", "converted_packet_ticks", "received_ticks",
        "position_valid", "data_discontinuity", "outcome", "written_bytes", "skipped_bytes", "zero_bytes",
        "fault_kind", "timeline_before", "timeline_after", "buffer_capacity_frames", "poll_sleep_ms"
    };
    private static readonly string[] TimelineColumns =
    {
        "anchor_ticks", "anchor_device", "anchor_media_bytes", "media_bytes", "last_written_device_start",
        "last_written_device_end", "trusted_qpc_ticks", "trusted_device", "pending_qpc_ticks", "pending_device",
        "outliers", "consecutive_outliers", "confirmed_phase_rebases", "absolute_phase_adjustment_ticks",
        "phase_budget_ticks", "jitter_tolerance_ticks", "max_device_gap_frames"
    };
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "version", "timestamp_frequency", "qpc_units_per_second", "helper_mvid", "naudio_core_mvid", "naudio_wasapi_mvid",
        "capacity", "total_observed", "omitted_packets", "fault_sequence", "fault_record_pinned", "columns", "timeline_columns",
        "packets", "states", "states_omitted"
    };

    public static bool TryRead(string? stderr, out JsonElement diagnostic)
    {
        diagnostic = default;
        if (string.IsNullOrEmpty(stderr)) return false;
        int index = stderr.IndexOf(Prefix, StringComparison.Ordinal);
        if (index < 0 || (index > 0 && stderr[index - 1] is not '\n' and not '\r')) return false;
        int start = index + Prefix.Length;
        int end = stderr.IndexOfAny(new[] { '\r', '\n' }, start);
        if (end < 0) end = stderr.Length;
        if (end - start > MaxJsonChars) return false;
        try
        {
            using var document = JsonDocument.Parse(stderr.Substring(start, end - start), new() { MaxDepth = 6 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var keys = root.EnumerateObject().Select(x => x.Name).ToArray();
            if (keys.Length != Fields.Count || !Fields.SetEquals(keys)) return false;
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("capacity").GetInt32() != 64 ||
                root.GetProperty("timestamp_frequency").GetInt64() <= 0 || root.GetProperty("qpc_units_per_second").GetInt64() != 10_000_000)
                return false;
            foreach (var key in new[] { "helper_mvid", "naudio_core_mvid", "naudio_wasapi_mvid" })
                if (!Guid.TryParseExact(root.GetProperty(key).GetString(), "D", out _)) return false;
            if (!NamesMatch(root.GetProperty("columns"), Columns) || !NamesMatch(root.GetProperty("timeline_columns"), TimelineColumns)) return false;
            var packets = root.GetProperty("packets");
            var states = root.GetProperty("states");
            if (packets.ValueKind != JsonValueKind.Array || packets.GetArrayLength() > 64 ||
                states.ValueKind != JsonValueKind.Array || states.GetArrayLength() > 8) return false;
            long previous = 0;
            foreach (var row in packets.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != Columns.Length) return false;
                for (int i = 0; i < row.GetArrayLength(); i++)
                {
                    var value = row[i];
                    if (i is 26 or 27) { if (value.ValueKind != JsonValueKind.Null && !NumericArray(value, 17)) return false; }
                    else if (i is 13 or 19 or 20) { if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)) return false; }
                    else if (value.ValueKind != JsonValueKind.Null && !value.TryGetInt64(out _)) return false;
                }
                long sequence = row[0].GetInt64();
                if (sequence <= previous || row[1].GetInt64() < 1 || row[21].GetInt32() is < 0 or > 6 || row[25].GetInt32() is < 0 or > 3) return false;
                previous = sequence;
            }
            foreach (var state in states.EnumerateArray()) if (!NumericArray(state, 4)) return false;
            long total = root.GetProperty("total_observed").GetInt64();
            if (total < previous || root.GetProperty("omitted_packets").GetInt64() != total - packets.GetArrayLength() ||
                root.GetProperty("states_omitted").GetInt64() < 0) return false;
            var fault = root.GetProperty("fault_sequence");
            bool pinned = root.GetProperty("fault_record_pinned").GetBoolean();
            if (pinned != (fault.ValueKind != JsonValueKind.Null) ||
                (pinned && !packets.EnumerateArray().Any(row => row[0].GetInt64() == fault.GetInt64() && row[21].GetInt32() == 4))) return false;
            diagnostic = root.Clone();
            return true;
        }
        catch { return false; }
    }

    private static bool NamesMatch(JsonElement value, string[] names) => value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() == names.Length && value.EnumerateArray().Select(x => x.GetString()).SequenceEqual(names);
    private static bool NumericArray(JsonElement value, int length) => value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() == length && value.EnumerateArray().All(x => x.TryGetInt64(out _));
}
