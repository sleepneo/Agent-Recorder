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
            using var document = JsonDocument.Parse(stderr.Substring(start, end - start), new() { MaxDepth = 10 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var keys = root.EnumerateObject().Select(x => x.Name).ToArray();
            bool idle = root.TryGetProperty("idle_evidence_version", out _);
            var expected = idle ? Fields.Concat(new[] { "idle_evidence_version", "idle_sources_omitted", "idle_sources" }).ToHashSet(StringComparer.Ordinal) : Fields;
            if (idle && root.GetProperty("idle_evidence_version").GetInt32() >= 2) expected.Add("idle_fault_binding");
            if (idle && root.GetProperty("idle_evidence_version").GetInt32() == 3) expected.Add("idle_policy");
            if (keys.Length != expected.Count || !expected.SetEquals(keys) || (idle && !ValidIdle(root))) return false;
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

    private static bool Keys(JsonElement e, params string[] keys) => e.ValueKind == JsonValueKind.Object &&
        e.EnumerateObject().Count() == keys.Length && e.EnumerateObject().All(p => keys.Contains(p.Name, StringComparer.Ordinal));
    private static bool ValidIdle(JsonElement root)
    {
        int version = root.GetProperty("idle_evidence_version").GetInt32();
        if (version is not (1 or 2 or 3) || root.GetProperty("idle_sources_omitted").GetInt64() < 0) return false;
        if(version==3 && !NumericArray(root.GetProperty("idle_policy"),13))return false;
        var sources = root.GetProperty("idle_sources");
        if (sources.ValueKind != JsonValueKind.Array || sources.GetArrayLength() > 4) return false;
        long generation = 0;
        foreach (var source in sources.EnumerateArray())
        {
            if (!Keys(source,"generation","evidence") || source.GetProperty("generation").GetInt64() <= generation) return false;
            generation = source.GetProperty("generation").GetInt64();
            var e = source.GetProperty("evidence");
            if (!Keys(e,"total_windows","omitted_windows","windows")) return false;
            var windows = e.GetProperty("windows");
            if (windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() > 4 ||
                e.GetProperty("total_windows").GetInt64() != e.GetProperty("omitted_windows").GetInt64() + windows.GetArrayLength() || e.GetProperty("omitted_windows").GetInt64() < 0) return false;
            foreach (var w in windows.EnumerateArray())
            {
                if (!Keys(w,"start_ticks","last_empty_ticks","end_ticks","empty_polls","end_reason","previous","following") ||
                    w.GetProperty("start_ticks").GetInt64() > w.GetProperty("last_empty_ticks").GetInt64() ||
                    w.GetProperty("empty_polls").GetInt64() <= 0 || w.GetProperty("end_reason").GetInt32() is < 0 or > 2 ||
                    !NumericArray(w.GetProperty("previous"),5)) return false;
                var packets = w.GetProperty("following");
                if (packets.ValueKind != JsonValueKind.Array || packets.GetArrayLength() > 4 || packets.EnumerateArray().Any(p => !NumericArray(p,6))) return false;
                _ = w.GetProperty("end_ticks").GetInt64();
            }
        }
        if (version >= 2)
        {
            var binding = root.GetProperty("idle_fault_binding");
            var fault = root.GetProperty("fault_sequence");
            if (fault.ValueKind == JsonValueKind.Null) return binding.ValueKind == JsonValueKind.Null;
            if (!NumericArray(binding,4) || binding[0].GetInt64() < 1 || binding[3].GetInt32() is < 0 or > 3) return false;
            var packet = root.GetProperty("packets").EnumerateArray().First(p => p[0].GetInt64() == fault.GetInt64());
            if (packet[1].GetInt64() != binding[0].GetInt64() || (packet[2].ValueKind == JsonValueKind.Null ? -1 : packet[2].GetInt64()) != binding[1].GetInt64()) return false;
            if (binding[3].GetInt32() == 1)
                return binding[1].GetInt64() > 0 && sources.EnumerateArray().Any(s => s.GetProperty("generation").GetInt64() == binding[0].GetInt64() &&
                    s.GetProperty("evidence").GetProperty("windows").EnumerateArray().Any(w => w.GetProperty("start_ticks").GetInt64() == binding[2].GetInt64() &&
                        w.GetProperty("following").EnumerateArray().Any(p => p[0].GetInt64() == binding[1].GetInt64())));
            return binding[2].GetInt64() == -1 && (binding[3].GetInt32() == 3 ? binding[1].GetInt64() <= 0 : binding[1].GetInt64() > 0);
        }
        return true;
    }
}
