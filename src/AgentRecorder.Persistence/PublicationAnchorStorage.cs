using System.Buffers.Binary;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class PublicationAnchorStorage
{
    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingHistorySnapshot row)
    {
        if (row.PublicationReceipt is not { } receipt) return;
        var anchor = receipt.Anchor;
        if (!MatchesRow(anchor, row)) throw new InvalidDataException("Publication receipt binding is invalid.");
        var volume = new byte[8]; var id = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(volume, anchor.Identity.VolumeSerialNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(id, anchor.Identity.IdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8), anchor.Identity.IdHigh);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO ordinary_publication_anchors VALUES($id,$path,$length,1,'staging_copy_handle/v1',$volume,$file);";
        command.Parameters.AddWithValue("$id", row.RecordingId); command.Parameters.AddWithValue("$path", row.OutputPath);
        command.Parameters.AddWithValue("$length", anchor.Length); command.Parameters.AddWithValue("$volume", volume);
        command.Parameters.AddWithValue("$file", id); command.ExecuteNonQuery();
    }
    internal static bool MatchesRow(PublicationIdentityAnchor anchor, OrdinaryRecordingHistorySnapshot row) =>
        anchor.Valid && row.Status == "completed" && row.CompletedAtUtc is not null &&
        row.OutputSizeBytes == anchor.Length && string.Equals(row.OutputPath, anchor.Path, StringComparison.OrdinalIgnoreCase);
    internal static Dictionary<string, PublicationIdentityAnchor?> Read(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows)
    {
        SqliteSchemaV30.Validate(connection, transaction);
        if (rows.Count > 100) throw new InvalidDataException("Publication page exceeds its bound.");
        var result = rows.ToDictionary(r => r.RecordingId, _ => (PublicationIdentityAnchor?)null, StringComparer.Ordinal);
        if (rows.Count == 0) return result;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "WITH requested(recording_id) AS (VALUES " +
            string.Join(",", rows.Select((_, i) => "($id" + i + ")")) + ") SELECT a.recording_id,a.output_path,a.output_size_bytes,a.observation_version,a.source,a.volume_id,a.file_id FROM requested JOIN ordinary_publication_anchors a ON a.recording_id=requested.recording_id;";
        for (var i = 0; i < rows.Count; i++) command.Parameters.AddWithValue("$id" + i, rows[i].RecordingId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (reader.GetValue(0) is not string || reader.GetValue(1) is not string || reader.GetValue(4) is not string ||
                !result.ContainsKey(key) || result[key] is not null || reader.GetValue(2) is not long || reader.GetValue(3) is not long || reader.GetInt64(3) != PublicationIdentityAnchor.Version ||
                reader.GetString(4) != PublicationIdentityAnchor.Source || reader.GetValue(5) is not byte[] volume || volume.Length != 8 ||
                reader.GetValue(6) is not byte[] id || id.Length != 16)
                throw new InvalidDataException("Publication anchor format is invalid.");
            var anchor = new PublicationIdentityAnchor(reader.GetString(1), reader.GetInt64(2),
                new(BinaryPrimitives.ReadUInt64LittleEndian(volume), BinaryPrimitives.ReadUInt64LittleEndian(id), BinaryPrimitives.ReadUInt64LittleEndian(id.AsSpan(8))));
            if (!MatchesRow(anchor, rows.Single(r => r.RecordingId == key))) throw new InvalidDataException("Publication anchor association is invalid.");
            result[key] = anchor;
        }
        return result;
    }
}
