using System.Buffers.Binary;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class OrdinaryMediaHashStorage
{
    internal static void ValidateUpdate(OrdinaryRecordingBundleUpdate update)
    {
        if (update.Result.MediaHashEvidence is not { } hash) return;
        var op = update.Operation;
        if (update.Result.Status != "ready" || update.Result.IdentityEvidence is null || update.Result.ContentsEvidence is null ||
            hash.RecordingId != op.RecordingId || hash.OperationId != op.OperationId || hash.MediaPath != op.MediaPath || hash.BundlePath != op.BundlePath)
            throw new ArgumentException("Media hash update binding is invalid.");
    }

    internal static void Persist(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingBundleUpdate update,
        OrdinaryRecordingHistorySnapshot row)
    {
        ValidateUpdate(update);
        var op = update.Operation; var hash = update.Result.MediaHashEvidence;
        if (hash is not null && PublicationAnchorStorage.Read(connection, transaction, new[] { row })[row.RecordingId] != hash.Anchor)
            throw new InvalidDataException("Media hash does not match the original publication anchor.");
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO ordinary_media_hash(recording_id,media_output_path,operation_id,bundle_path)
            SELECT $id,$media,$op,$path WHERE NOT EXISTS(SELECT 1 FROM ordinary_media_hash WHERE recording_id=$id);
            UPDATE ordinary_media_hash SET evidence_version=$version,algorithm=$algorithm,hash_value=$value,basis=$basis,
                size_bytes=$size,volume_id=$volume,file_id=$file
            WHERE recording_id=$id AND media_output_path=$media AND operation_id=$op AND bundle_path=$path AND evidence_version IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", op.RecordingId); cmd.Parameters.AddWithValue("$media", op.MediaPath);
        cmd.Parameters.AddWithValue("$op", op.OperationId); cmd.Parameters.AddWithValue("$path", op.BundlePath);
        byte[]? volume = null, file = null;
        if (hash is not null)
        {
            volume = new byte[8]; file = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(volume, hash.Anchor.Identity.VolumeSerialNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(file, hash.Anchor.Identity.IdLow);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(8), hash.Anchor.Identity.IdHigh);
        }
        cmd.Parameters.AddWithValue("$version", hash is null ? DBNull.Value : 1);
        cmd.Parameters.AddWithValue("$algorithm", hash is null ? DBNull.Value : BundleMediaHashEvidence.Algorithm);
        cmd.Parameters.AddWithValue("$value", (object?)hash?.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$basis", hash is null ? DBNull.Value : BundleMediaHashEvidence.Basis);
        cmd.Parameters.AddWithValue("$size", hash is null ? DBNull.Value : hash.Anchor.Length);
        cmd.Parameters.AddWithValue("$volume", (object?)volume ?? DBNull.Value); cmd.Parameters.AddWithValue("$file", (object?)file ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() is not (1 or 2)) throw new InvalidDataException("Media hash marker cannot be committed.");
    }

    internal static void Read(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows,
        Dictionary<string, OrdinaryRecordingBundleStorage.Fact?> facts,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? observer = null)
    {
        SqliteSchemaV36.Validate(connection, transaction);
        if (rows.Count > 100) throw new InvalidDataException("Media hash page exceeds bound.");
        if (rows.Count == 0) return;
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "WITH requested(recording_id) AS (VALUES " + string.Join(",", rows.Select((_, i) => "($id" + i + ")")) + ") " + """
            SELECT requested.recording_id,b.rowid,h.recording_id,h.media_output_path,h.operation_id,h.bundle_path,h.evidence_version,
                h.algorithm,h.hash_value,h.basis,h.size_bytes,h.volume_id,h.file_id,boundary.legacy_bundle_rowid,
                a.output_path,a.output_size_bytes,a.observation_version,a.source,a.volume_id,a.file_id
            FROM requested
            LEFT JOIN ordinary_recording_bundles b ON b.recording_id=requested.recording_id
            LEFT JOIN ordinary_media_hash h INDEXED BY idx_ordinary_media_hash_recording ON h.recording_id=requested.recording_id
            LEFT JOIN ordinary_publication_anchors a ON a.recording_id=requested.recording_id
            CROSS JOIN ordinary_media_hash_boundary boundary LIMIT 101;
            """;
        for (var i = 0; i < rows.Count; i++) cmd.Parameters.AddWithValue("$id" + i, rows[i].RecordingId);
        observer?.Invoke(connection, transaction, cmd.CommandText, cmd.Parameters.Cast<SqliteParameter>().Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        using var reader = cmd.ExecuteReader(); var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var key = reader.GetString(0);
            if (!seen.Add(key) || seen.Count > 100) throw new InvalidDataException("Invalid media hash page.");
            var fact = facts[key]; var marker = !reader.IsDBNull(2); BundleMediaHashEvidence? hash = null;
            if (fact is null)
            {
                if (!reader.IsDBNull(1) || marker) throw new InvalidDataException("Orphaned media hash.");
            }
            else if (!marker)
            {
                if (reader.IsDBNull(1) || reader.GetInt64(1) > reader.GetInt64(13)) throw new InvalidDataException("Required media hash marker is missing.");
            }
            else
            {
                if (Text(reader, 3) != fact.MediaPath || Text(reader, 4) != fact.OperationId || Text(reader, 5) != fact.Snapshot.Path)
                    throw new InvalidDataException("Media hash association is invalid.");
                if (reader.IsDBNull(6))
                {
                    for (var i = 7; i <= 12; i++) if (!reader.IsDBNull(i)) throw new InvalidDataException("Unregistered media hash fields.");
                }
                else
                {
                    if (reader.GetValue(6) is not long version || version != 1 || Text(reader, 7) != BundleMediaHashEvidence.Algorithm ||
                        reader.GetValue(8) is not string value || !BundleMediaHashEvidence.ValidValue(value) || Text(reader, 9) != BundleMediaHashEvidence.Basis ||
                        reader.GetValue(10) is not long size || size <= 0 || reader.GetValue(11) is not byte[] volume || volume.Length != 8 ||
                        reader.GetValue(12) is not byte[] file || file.Length != 16 || fact.Snapshot.Status != "ready" ||
                        fact.Snapshot.IdentityEvidence is null || fact.Snapshot.ContentsEvidence is null || fact.OperationId is null || fact.Snapshot.Path is null ||
                        reader.GetValue(14) is not string path || !string.Equals(path, fact.MediaPath, StringComparison.OrdinalIgnoreCase) ||
                        reader.GetValue(15) is not long originalSize || originalSize != size || reader.GetValue(16) is not long originalVersion || originalVersion != 1 ||
                        Text(reader, 17) != PublicationIdentityAnchor.Source || reader.GetValue(18) is not byte[] originalVolume || !originalVolume.SequenceEqual(volume) ||
                        reader.GetValue(19) is not byte[] originalFile || !originalFile.SequenceEqual(file))
                        throw new InvalidDataException("Invalid media hash format or publication binding.");
                    var anchor = new PublicationIdentityAnchor(path, size, new(BinaryPrimitives.ReadUInt64LittleEndian(volume),
                        BinaryPrimitives.ReadUInt64LittleEndian(file), BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8))));
                    if (!PublicationAnchorStorage.MatchesRow(anchor, rows.Single(r => r.RecordingId == key))) throw new InvalidDataException("Invalid media hash media binding.");
                    hash = BundleMediaHashEvidence.FromStored(key, fact.OperationId, fact.MediaPath, fact.Snapshot.Path, value, anchor);
                }
            }
            if (fact is not null) facts[key] = fact with { Snapshot = fact.Snapshot with { MediaHashEvidence = hash } };
        }
        if (seen.Count != rows.Count) throw new InvalidDataException("Incomplete media hash page.");
    }

    internal static void PrepareRecovery(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteSchemaV36.Validate(connection, transaction);
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM ordinary_recording_bundles b
              LEFT JOIN ordinary_media_hash h ON h.recording_id=b.recording_id
              CROSS JOIN ordinary_media_hash_boundary boundary WHERE b.status_code='generating' AND (
              (h.recording_id IS NULL AND b.rowid>boundary.legacy_bundle_rowid) OR
              (h.recording_id IS NOT NULL AND (h.media_output_path IS NOT b.media_output_path OR h.operation_id IS NOT b.operation_id OR
              h.bundle_path IS NOT b.bundle_path OR h.evidence_version IS NOT NULL OR h.algorithm IS NOT NULL OR h.hash_value IS NOT NULL OR
              h.basis IS NOT NULL OR h.size_bytes IS NOT NULL OR h.volume_id IS NOT NULL OR h.file_id IS NOT NULL))));
            """;
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) throw new InvalidDataException("Invalid media hash recovery binding.");
        cmd.CommandText = "UPDATE ordinary_media_hash SET operation_id=NULL WHERE recording_id IN (SELECT recording_id FROM ordinary_recording_bundles WHERE status_code='generating');";
        cmd.ExecuteNonQuery();
    }
    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetValue(i) as string ?? throw new InvalidDataException("Invalid media hash text type.");
}
