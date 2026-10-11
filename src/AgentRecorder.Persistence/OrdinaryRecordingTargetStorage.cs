using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class OrdinaryRecordingTargetStorage
{
    internal static void ValidateBinding(OrdinaryRecordingHistorySnapshot row, string? key)
    {
        if (key is null) return;
        if (OrdinaryWindowTargetKey.IsValid(key))
        {
            if (row.SourceType != "window" || row.CaptureSemantics != "window_surface" || row.NestedRole is not null)
                throw new InvalidDataException("Window target binding is invalid.");
            return;
        }
        var kind = row.SourceType == "fixed_region_profile" ? "region" : row.SourceType;
        if (!OrdinaryScreenTargetKey.IsValid(key) || kind is not ("display" or "region") ||
            !key.StartsWith("target:v1:" + kind + ":", StringComparison.Ordinal) ||
            kind == "display" && row.CaptureSemantics is not ("display_capture" or "display_surface" or "screen_rectangle") ||
            kind == "region" && row.CaptureSemantics is not ("desktop_region" or "region_rectangle" or "screen_rectangle"))
            throw new InvalidDataException("Target binding is invalid.");
    }

    internal static void SetInitial(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingHistorySnapshot row)
    {
        SqliteSchemaV32.Validate(connection, transaction);
        SqliteSchemaV34.Validate(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE ordinary_recording_targets SET target_key=$key WHERE recording_id=$id AND created_at_utc=$created AND target_key IS NULL;";
        var window = OrdinaryWindowTargetKey.IsValid(row.TargetKey);
        command.Parameters.AddWithValue("$key", !window && row.TargetKey is not null ? row.TargetKey : DBNull.Value);
        command.Parameters.AddWithValue("$id", row.RecordingId); command.Parameters.AddWithValue("$created", row.CreatedAtUtc.UtcTicks);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Initial target fact is missing.");
        command.CommandText = "UPDATE ordinary_recording_window_targets SET target_key=$key WHERE recording_id=$id AND created_at_utc=$created AND source_type=$source AND capture_semantics=$semantics AND target_key IS NULL;";
        command.Parameters["$key"].Value = window ? row.TargetKey! : DBNull.Value;
        command.Parameters.AddWithValue("$source", row.SourceType); command.Parameters.AddWithValue("$semantics", row.CaptureSemantics);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Initial window target fact is missing.");
    }

    internal static Dictionary<string, string?> Read(SqliteConnection connection, SqliteTransaction? transaction,
        IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows, Action<string, IReadOnlyList<KeyValuePair<string, object>>>? observer = null)
    {
        try { return ReadCore(connection, transaction, rows, observer); }
        catch (Exception e) { throw new OrdinaryRecordingHistoryUnavailableException("Target history is unavailable.", e); }
    }

    private static Dictionary<string, string?> ReadCore(SqliteConnection connection, SqliteTransaction? transaction,
        IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows, Action<string, IReadOnlyList<KeyValuePair<string, object>>>? observer)
    {
        SqliteSchemaV32.Validate(connection, transaction);
        SqliteSchemaV34.Validate(connection, transaction);
        if (rows.Count > 100) throw new InvalidDataException("Target page exceeds its bound.");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (rows.Count == 0) return result;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "WITH requested(recording_id) AS (VALUES " + string.Join(",", rows.Select((_, i) => "($id" + i + ")")) + ") " +
            "SELECT h.recording_id,h.history_sequence,t.recording_id,t.created_at_utc,t.target_key,b.legacy_sequence," +
            "w.recording_id,w.created_at_utc,w.source_type,w.capture_semantics,w.target_key,wb.legacy_sequence " +
            "FROM requested r JOIN ordinary_recording_history h ON h.recording_id=r.recording_id " +
            "LEFT JOIN ordinary_recording_targets t ON t.recording_id=h.recording_id " +
            "LEFT JOIN ordinary_recording_window_targets w ON w.recording_id=h.recording_id " +
            "CROSS JOIN ordinary_recording_target_boundary b CROSS JOIN ordinary_window_target_boundary wb;";
        for (var i = 0; i < rows.Count; i++) command.Parameters.AddWithValue("$id" + i, rows[i].RecordingId);
        observer?.Invoke(command.CommandText, command.Parameters.Cast<SqliteParameter>()
            .Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        var expected = rows.ToDictionary(r => r.RecordingId, StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0); var row = expected[id];
            if (reader.IsDBNull(2) && reader.GetInt64(1) > reader.GetInt64(5) ||
                !reader.IsDBNull(2) && reader.GetInt64(3) != row.CreatedAtUtc.UtcTicks)
                throw new InvalidDataException("Target association is invalid.");
            var key = reader.IsDBNull(4) ? null : reader.GetString(4);
            if (reader.IsDBNull(6) && reader.GetInt64(1) > reader.GetInt64(11) ||
                !reader.IsDBNull(6) && (reader.GetInt64(7) != row.CreatedAtUtc.UtcTicks ||
                    reader.GetString(8) != row.SourceType || reader.GetString(9) != row.CaptureSemantics))
                throw new InvalidDataException("Window target association is invalid.");
            var windowKey = reader.IsDBNull(10) ? null : reader.GetString(10);
            if (windowKey is not null && (!OrdinaryWindowTargetKey.IsValid(windowKey) || key is not null))
                throw new InvalidDataException("Conflicting target namespaces.");
            key ??= windowKey;
            ValidateBinding(row, key);
            if (!result.TryAdd(id, key)) throw new InvalidDataException("Duplicate target association.");
        }
        if (result.Count != rows.Count) throw new InvalidDataException("Missing target association.");
        return result;
    }
}
