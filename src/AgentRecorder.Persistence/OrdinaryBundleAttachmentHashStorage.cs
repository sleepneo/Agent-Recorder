using System.Buffers.Binary;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class OrdinaryBundleAttachmentHashStorage
{
    internal static void ValidateUpdate(OrdinaryRecordingBundleUpdate update)
    {
        if (update.Result.ContentHashEvidence is not { } hashes) return;
        var identity = update.Result.IdentityEvidence;
        var op = update.Operation;
        if (update.Result.Status != "ready" || update.Result.ContentsEvidence is null || identity is null ||
            !hashes.Identity.Equals(identity) || identity.RecordingId != op.RecordingId || identity.OperationId != op.OperationId ||
            identity.MediaPath != op.MediaPath || identity.BundlePath != op.BundlePath)
            throw new ArgumentException("Attachment digest publication binding is invalid.");
        OrdinaryBundleIdentityStorage.ValidateUpdate(update);
    }

    internal static void Persist(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingBundleUpdate update)
    {
        ValidateUpdate(update);
        var op = update.Operation; var hashes = update.Result.ContentHashEvidence;
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO ordinary_bundle_attachment_hash(recording_id,media_output_path,operation_id,bundle_path,evidence_version,item_count)
            SELECT $id,$media,$op,$path,NULL,0 WHERE NOT EXISTS(SELECT 1 FROM ordinary_bundle_attachment_hash WHERE recording_id=$id);
            UPDATE ordinary_bundle_attachment_hash SET evidence_version=$version,item_count=$count
            WHERE recording_id=$id AND media_output_path=$media AND operation_id=$op AND bundle_path=$path AND evidence_version IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", op.RecordingId); cmd.Parameters.AddWithValue("$media", op.MediaPath);
        cmd.Parameters.AddWithValue("$op", op.OperationId); cmd.Parameters.AddWithValue("$path", op.BundlePath);
        cmd.Parameters.AddWithValue("$version", hashes is null ? DBNull.Value : 1);
        cmd.Parameters.AddWithValue("$count", hashes is null ? 0 : 5);
        if (cmd.ExecuteNonQuery() is not (1 or 2)) throw new InvalidDataException("Attachment digest marker cannot be committed.");
        if (hashes is null) return;
        for (var i = 0; i < 5; i++)
        {
            var anchor = hashes.Identity.Anchors[i]; var volume = new byte[8]; var file = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(volume, anchor.Identity.VolumeSerialNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(file, anchor.Identity.IdLow);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(8), anchor.Identity.IdHigh);
            cmd.Parameters.Clear();
            cmd.CommandText = "INSERT INTO ordinary_bundle_attachment_hash_items VALUES($id,$ordinal,$name,$path,$size,'sha256',$value,'bundle_publication',$volume,$file);";
            cmd.Parameters.AddWithValue("$id", op.RecordingId); cmd.Parameters.AddWithValue("$ordinal", i);
            cmd.Parameters.AddWithValue("$name", update.Result.ContentsEvidence!.Contents[i].Name);
            cmd.Parameters.AddWithValue("$path", anchor.Path); cmd.Parameters.AddWithValue("$size", anchor.Length);
            cmd.Parameters.AddWithValue("$value", hashes.Values[i]); cmd.Parameters.AddWithValue("$volume", volume);
            cmd.Parameters.AddWithValue("$file", file); cmd.ExecuteNonQuery();
        }
    }

    internal static void Read(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows,
        Dictionary<string, OrdinaryRecordingBundleStorage.Fact?> facts,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? observer = null)
    {
        SqliteSchemaV37.Validate(connection, transaction);
        if (rows.Count > 100) throw new InvalidDataException("Attachment digest page exceeds bound.");
        if (rows.Count == 0) return;
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "WITH requested(recording_id) AS (VALUES " + string.Join(",", rows.Select((_, i) => "($id" + i + ")")) + ") " + """
            SELECT requested.recording_id,b.rowid,h.recording_id,h.media_output_path,h.operation_id,h.bundle_path,h.evidence_version,h.item_count,
                i.recording_id,i.ordinal,i.name,i.output_path,i.output_size_bytes,i.algorithm,i.hash_value,i.basis,i.volume_id,i.file_id,
                boundary.legacy_bundle_rowid
            FROM requested
            LEFT JOIN ordinary_recording_bundles b ON b.recording_id=requested.recording_id
            LEFT JOIN ordinary_bundle_attachment_hash h ON h.recording_id=requested.recording_id
            LEFT JOIN ordinary_bundle_attachment_hash_items i INDEXED BY idx_ordinary_bundle_attachment_hash_ordinal ON i.recording_id=requested.recording_id
            CROSS JOIN ordinary_bundle_attachment_hash_boundary boundary
            ORDER BY requested.recording_id COLLATE BINARY,i.ordinal ASC LIMIT 501;
            """;
        for (var i = 0; i < rows.Count; i++) cmd.Parameters.AddWithValue("$id" + i, rows[i].RecordingId);
        observer?.Invoke(connection, transaction, cmd.CommandText, cmd.Parameters.Cast<SqliteParameter>()
            .Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        var versions = new Dictionary<string, long?>(StringComparer.Ordinal);
        var values = rows.ToDictionary(r => r.RecordingId, _ => new List<string>(5), StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader())
        {
            var count = 0;
            while (reader.Read())
            {
                if (++count > 500) throw new InvalidDataException("Attachment digest result exceeds bound.");
                var key = Text(reader, 0)!; var fact = facts[key]; var marker = !reader.IsDBNull(2); long? version = null;
                if (fact is null)
                {
                    if (!reader.IsDBNull(1) || marker || !reader.IsDBNull(8)) throw new InvalidDataException("Orphaned attachment digest.");
                }
                else if (!marker)
                {
                    if (reader.GetValue(1) is not long rowid || rowid > reader.GetInt64(18) || !reader.IsDBNull(8))
                        throw new InvalidDataException("Required attachment digest marker is missing.");
                }
                else
                {
                    if (Text(reader, 2) != key || reader.GetValue(7) is not long items ||
                        (!reader.IsDBNull(6) && reader.GetValue(6) is not long)) throw new InvalidDataException("Invalid attachment digest marker type.");
                    version = reader.IsDBNull(6) ? null : reader.GetInt64(6);
                    if (Text(reader, 3) != fact.MediaPath || Text(reader, 4) != fact.OperationId || Text(reader, 5) != fact.Snapshot.Path ||
                        (version is null ? items != 0 || !reader.IsDBNull(8) : version != 1 || items != 5 || fact.Snapshot.Status != "ready" ||
                            fact.Snapshot.ContentsEvidence is null || fact.Snapshot.IdentityEvidence is null))
                        throw new InvalidDataException("Invalid attachment digest association.");
                }
                if (versions.TryGetValue(key, out var previous) && previous != version) throw new InvalidDataException("Mixed attachment digest versions.");
                versions[key] = version;
                if (!reader.IsDBNull(8))
                {
                    var ordinal = values[key].Count;
                    if (ordinal >= 5 || Text(reader, 8) != key || reader.GetValue(9) is not long storedOrdinal || storedOrdinal != ordinal ||
                        Text(reader, 10) != fact!.Snapshot.ContentsEvidence!.Contents[ordinal].Name ||
                        Text(reader, 11) != fact.Snapshot.IdentityEvidence!.Anchors[ordinal].Path ||
                        reader.GetValue(12) is not long size || size != fact.Snapshot.ContentsEvidence.Contents[ordinal].SizeBytes ||
                        size != fact.Snapshot.IdentityEvidence.Anchors[ordinal].Length || Text(reader, 13) != BundlePublicationReceipt.AttachmentHashes.Algorithm ||
                        reader.GetValue(14) is not string value || !BundleMediaHashEvidence.ValidValue(value) ||
                        Text(reader, 15) != BundlePublicationReceipt.AttachmentHashes.Basis ||
                        reader.GetValue(16) is not byte[] volume || volume.Length != 8 || reader.GetValue(17) is not byte[] file || file.Length != 16)
                        throw new InvalidDataException("Invalid attachment digest item format or binding.");
                    var identity = fact.Snapshot.IdentityEvidence.Anchors[ordinal].Identity;
                    if (BinaryPrimitives.ReadUInt64LittleEndian(volume) != identity.VolumeSerialNumber ||
                        BinaryPrimitives.ReadUInt64LittleEndian(file) != identity.IdLow || BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8)) != identity.IdHigh)
                        throw new InvalidDataException("Attachment digest original identity differs.");
                    values[key].Add(value);
                }
            }
        }
        if (versions.Count != rows.Count) throw new InvalidDataException("Incomplete attachment digest page.");
        foreach (var row in rows)
        {
            var list = values[row.RecordingId]; BundlePublicationReceipt.AttachmentHashes? hashes = null;
            if (versions[row.RecordingId] == 1)
            {
                if (list.Count != 5) throw new InvalidDataException("Incomplete attachment digest set.");
                hashes = BundlePublicationReceipt.AttachmentHashes.FromStored(facts[row.RecordingId]!.Snapshot.IdentityEvidence!, list);
            }
            else if (list.Count != 0) throw new InvalidDataException("Unregistered attachment digests.");
            if (facts[row.RecordingId] is { } fact) facts[row.RecordingId] = fact with { Snapshot = fact.Snapshot with { ContentHashEvidence = hashes } };
        }
    }

    internal static void PrepareRecovery(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteSchemaV37.Validate(connection, transaction);
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM ordinary_recording_bundles b
              LEFT JOIN ordinary_bundle_attachment_hash h ON h.recording_id=b.recording_id
              CROSS JOIN ordinary_bundle_attachment_hash_boundary boundary WHERE b.status_code='generating' AND (
              (h.recording_id IS NULL AND b.rowid>boundary.legacy_bundle_rowid) OR
              EXISTS(SELECT 1 FROM ordinary_bundle_attachment_hash_items i WHERE i.recording_id=b.recording_id) OR
              (h.recording_id IS NOT NULL AND (h.media_output_path IS NOT b.media_output_path OR h.operation_id IS NOT b.operation_id OR
              h.bundle_path IS NOT b.bundle_path OR h.evidence_version IS NOT NULL OR h.item_count!=0))));
            """;
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) throw new InvalidDataException("Invalid attachment digest recovery binding.");
        cmd.CommandText = "UPDATE ordinary_bundle_attachment_hash SET operation_id=NULL WHERE recording_id IN (SELECT recording_id FROM ordinary_recording_bundles WHERE status_code='generating');";
        cmd.ExecuteNonQuery();
    }
    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null :
        reader.GetValue(ordinal) as string ?? throw new InvalidDataException("Invalid attachment digest text type.");
}
