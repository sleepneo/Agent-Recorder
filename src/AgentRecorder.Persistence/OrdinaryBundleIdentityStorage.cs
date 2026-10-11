using System.Buffers.Binary;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class OrdinaryBundleIdentityStorage
{
    internal static void ValidateUpdate(OrdinaryRecordingBundleUpdate update)
    {
        if (update.Result.IdentityEvidence is not { } evidence) return;
        var op = update.Operation;
        if (update.Result.Status != "ready" || update.Result.ContentsEvidence is not { } contents ||
            evidence.RecordingId != op.RecordingId || evidence.OperationId != op.OperationId ||
            evidence.MediaPath != op.MediaPath || evidence.BundlePath != op.BundlePath)
            throw new ArgumentException("Bundle identity update is invalid.");
        try { _ = new BundleIdentityEvidence(op.RecordingId, op.OperationId, op.MediaPath, op.BundlePath, contents, evidence.Anchors); }
        catch (InvalidDataException ex) { throw new ArgumentException("Bundle identity update is invalid.", ex); }
    }

    internal static void Persist(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingBundleUpdate update)
    {
        ValidateUpdate(update);
        var op = update.Operation; var evidence = update.Result.IdentityEvidence;
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO ordinary_bundle_identity(recording_id,media_output_path,operation_id,bundle_path,evidence_version,item_count)
            SELECT $id,$media,$op,$path,NULL,0 WHERE NOT EXISTS(SELECT 1 FROM ordinary_bundle_identity WHERE recording_id=$id);
            UPDATE ordinary_bundle_identity SET evidence_version=$version,item_count=$count
            WHERE recording_id=$id AND media_output_path=$media AND operation_id=$op AND bundle_path=$path AND evidence_version IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", op.RecordingId); cmd.Parameters.AddWithValue("$media", op.MediaPath);
        cmd.Parameters.AddWithValue("$op", op.OperationId); cmd.Parameters.AddWithValue("$path", op.BundlePath);
        cmd.Parameters.AddWithValue("$version", evidence is null ? DBNull.Value : BundleIdentityEvidence.Version);
        cmd.Parameters.AddWithValue("$count", evidence is null ? 0 : 5);
        if (cmd.ExecuteNonQuery() is not (1 or 2)) throw new InvalidDataException("Bundle identity marker cannot be committed.");
        if (evidence is null) return;
        for (var i = 0; i < 5; i++)
        {
            var anchor = evidence.Anchors[i]; var volume = new byte[8]; var id = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(volume, anchor.Identity.VolumeSerialNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(id, anchor.Identity.IdLow);
            BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8), anchor.Identity.IdHigh);
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO ordinary_bundle_identity_items VALUES($id,$ordinal,$name,$path,$length,1,'bundle_copy_handle/v1',$volume,$file);";
            cmd.Parameters.AddWithValue("$id", op.RecordingId); cmd.Parameters.AddWithValue("$ordinal", i);
            cmd.Parameters.AddWithValue("$name", update.Result.ContentsEvidence!.Contents[i].Name);
            cmd.Parameters.AddWithValue("$path", anchor.Path); cmd.Parameters.AddWithValue("$length", anchor.Length);
            cmd.Parameters.AddWithValue("$volume", volume); cmd.Parameters.AddWithValue("$file", id); cmd.ExecuteNonQuery();
        }
    }

    internal static void Read(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows,
        Dictionary<string, OrdinaryRecordingBundleStorage.Fact?> facts,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? observer = null)
    {
        SqliteSchemaV35.Validate(connection, transaction);
        if (rows.Count > 100) throw new InvalidDataException("Bundle identity page exceeds bound.");
        if (rows.Count == 0) return;
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "WITH requested(recording_id) AS (VALUES " + string.Join(",", rows.Select((_, i) => "($id" + i + ")")) + ") " + """
            SELECT requested.recording_id,b.rowid,c.recording_id,c.media_output_path,c.operation_id,c.bundle_path,c.evidence_version,c.item_count,
                i.recording_id,i.ordinal,i.name,i.output_path,i.output_size_bytes,i.observation_version,i.source,i.volume_id,i.file_id,boundary.legacy_bundle_rowid
            FROM requested
            LEFT JOIN ordinary_recording_bundles b ON b.recording_id=requested.recording_id
            LEFT JOIN ordinary_bundle_identity c ON c.recording_id=requested.recording_id
            LEFT JOIN ordinary_bundle_identity_items i INDEXED BY idx_ordinary_bundle_identity_ordinal ON i.recording_id=requested.recording_id
            CROSS JOIN ordinary_bundle_identity_boundary boundary LIMIT 501;
            """;
        for (var i = 0; i < rows.Count; i++) cmd.Parameters.AddWithValue("$id" + i, rows[i].RecordingId);
        observer?.Invoke(connection, transaction, cmd.CommandText, cmd.Parameters.Cast<SqliteParameter>().Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        var versions = new Dictionary<string, long?>(StringComparer.Ordinal);
        var anchors = rows.ToDictionary(r => r.RecordingId, _ => new List<(long Ordinal, string Name, PublicationIdentityAnchor Anchor)>(), StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader())
        {
            var read = 0;
            while (reader.Read())
            {
                if (++read > 500) throw new InvalidDataException("Bundle identity result exceeds bound.");
                var key = reader.GetString(0); var fact = facts[key]; var marker = !reader.IsDBNull(2); long? version = null;
                if (fact is null)
                {
                    if (!reader.IsDBNull(1) || marker || !reader.IsDBNull(8)) throw new InvalidDataException("Orphaned Bundle identity.");
                }
                else if (!marker)
                {
                    if (reader.IsDBNull(1) || reader.GetInt64(1) > reader.GetInt64(17) || !reader.IsDBNull(8))
                        throw new InvalidDataException("Required Bundle identity marker is missing.");
                }
                else
                {
                    if (reader.GetValue(7) is not long count || (!reader.IsDBNull(6) && reader.GetValue(6) is not long))
                        throw new InvalidDataException("Invalid Bundle identity marker type.");
                    version = reader.IsDBNull(6) ? null : reader.GetInt64(6);
                    if (reader.GetString(3) != fact.MediaPath || Text(reader, 4) != fact.OperationId || Text(reader, 5) != fact.Snapshot.Path ||
                        (version is null ? count != 0 || !reader.IsDBNull(8) : version != 1 || count != 5 ||
                            fact.Snapshot.Status != "ready" || fact.Snapshot.ContentsEvidence is null))
                        throw new InvalidDataException("Invalid Bundle identity association.");
                }
                versions[key] = version;
                if (!reader.IsDBNull(8))
                {
                    if (reader.GetValue(9) is not long ordinal || reader.GetValue(12) is not long length || length <= 0 ||
                        reader.GetValue(13) is not long observationVersion || observationVersion != 1 || reader.GetString(14) != BundleIdentityEvidence.Source ||
                        reader.GetValue(15) is not byte[] volume || volume.Length != 8 || reader.GetValue(16) is not byte[] id || id.Length != 16)
                        throw new InvalidDataException("Invalid Bundle anchor format.");
                    anchors[key].Add((ordinal, reader.GetString(10), new(reader.GetString(11), length,
                        new(BinaryPrimitives.ReadUInt64LittleEndian(volume), BinaryPrimitives.ReadUInt64LittleEndian(id), BinaryPrimitives.ReadUInt64LittleEndian(id.AsSpan(8))))));
                    if (anchors[key].Count > 5) throw new InvalidDataException("Too many Bundle anchors.");
                }
            }
        }
        if (versions.Count != rows.Count) throw new InvalidDataException("Incomplete Bundle identity page.");
        foreach (var row in rows)
        {
            var list = anchors[row.RecordingId].OrderBy(i => i.Ordinal).ToArray();
            BundleIdentityEvidence? evidence = null;
            if (versions[row.RecordingId] == 1)
            {
                var fact = facts[row.RecordingId]!; var contents = fact.Snapshot.ContentsEvidence!;
                if (list.Length != 5 || list.Where((item, i) => item.Ordinal != i || item.Name != contents.Contents[i].Name).Any())
                    throw new InvalidDataException("Incomplete or unordered Bundle identity.");
                evidence = new(row.RecordingId, fact.OperationId!, fact.MediaPath, fact.Snapshot.Path!, contents, list.Select(i => i.Anchor));
            }
            else if (list.Length != 0) throw new InvalidDataException("Unregistered Bundle anchors.");
            if (facts[row.RecordingId] is { } value) facts[row.RecordingId] = value with { Snapshot = value.Snapshot with { IdentityEvidence = evidence } };
        }
    }
    internal static void PrepareRecovery(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteSchemaV35.Validate(connection, transaction);
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM ordinary_recording_bundles b
              LEFT JOIN ordinary_bundle_identity c ON c.recording_id=b.recording_id
              CROSS JOIN ordinary_bundle_identity_boundary boundary WHERE b.status_code='generating' AND (
              (c.recording_id IS NULL AND b.rowid>boundary.legacy_bundle_rowid) OR
              EXISTS(SELECT 1 FROM ordinary_bundle_identity_items i WHERE i.recording_id=b.recording_id) OR
              (c.recording_id IS NOT NULL AND (c.media_output_path IS NOT b.media_output_path OR c.operation_id IS NOT b.operation_id OR
              c.bundle_path IS NOT b.bundle_path OR c.evidence_version IS NOT NULL OR c.item_count!=0))));
            """;
        if (Convert.ToInt64(cmd.ExecuteScalar()) != 0) throw new InvalidDataException("Invalid Bundle identity recovery binding.");
        cmd.CommandText = "UPDATE ordinary_bundle_identity SET operation_id=NULL WHERE recording_id IN (SELECT recording_id FROM ordinary_recording_bundles WHERE status_code='generating');";
        cmd.ExecuteNonQuery();
    }
    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
}
