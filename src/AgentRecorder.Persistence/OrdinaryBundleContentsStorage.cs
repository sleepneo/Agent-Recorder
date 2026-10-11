using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class OrdinaryBundleContentsStorage
{
    internal static void Read(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows, Dictionary<string, OrdinaryRecordingBundleStorage.Fact?> facts,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? observer = null)
    {
        SqliteSchemaV33.Validate(connection, transaction);
        if (rows.Count > 100) throw new InvalidDataException("Contents page exceeds its bound.");
        if (rows.Count == 0) return;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "WITH requested(recording_id) AS (VALUES " + string.Join(",", rows.Select((_, i) => "($id" + i + ")")) + ") " + """
            SELECT requested.recording_id,b.rowid,c.recording_id,c.media_output_path,c.operation_id,c.bundle_path,
                   c.evidence_version,c.contents_count,i.recording_id,i.ordinal,i.name,i.media_type,i.size_bytes,boundary.legacy_bundle_rowid
            FROM requested
            LEFT JOIN ordinary_recording_bundles b ON b.recording_id=requested.recording_id
            LEFT JOIN ordinary_bundle_contents c ON c.recording_id=requested.recording_id
            LEFT JOIN ordinary_bundle_contents_items i INDEXED BY idx_ordinary_bundle_contents_ordinal ON i.recording_id=requested.recording_id
            CROSS JOIN ordinary_bundle_contents_boundary boundary
            LIMIT 501;
            """;
        for (var i = 0; i < rows.Count; i++) command.Parameters.AddWithValue("$id" + i, rows[i].RecordingId);
        observer?.Invoke(connection, transaction, command.CommandText, command.Parameters.Cast<SqliteParameter>()
            .Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        var items = rows.ToDictionary(r => r.RecordingId, _ => new List<(long Ordinal, RecordingBundleContentItem Item)>(), StringComparer.Ordinal);
        var versions = new Dictionary<string, long?>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            var read = 0;
            while (reader.Read())
            {
                if (++read > 500) throw new InvalidDataException("Contents result exceeds its bound.");
                var id = reader.GetString(0);
                var fact = facts[id];
                var marker = !reader.IsDBNull(2);
                long? version = null;
                if (fact is null)
                {
                    if (!reader.IsDBNull(1) || marker || !reader.IsDBNull(8)) throw new InvalidDataException("Orphaned contents binding.");
                }
                else if (!marker)
                {
                    if (reader.IsDBNull(1) || reader.GetInt64(1) > reader.GetInt64(13) || !reader.IsDBNull(8))
                        throw new InvalidDataException("Required contents binding is missing.");
                }
                else
                {
                    if (reader.GetValue(7) is not long count ||
                        (!reader.IsDBNull(6) && reader.GetValue(6) is not long))
                        throw new InvalidDataException("Invalid contents version or count type.");
                    version = reader.IsDBNull(6) ? null : reader.GetInt64(6);
                    if (reader.GetString(3) != fact.MediaPath || Text(reader, 4) != fact.OperationId || Text(reader, 5) != fact.Snapshot.Path ||
                        (version is null ? count != 0 || !reader.IsDBNull(8) : version != 1 || count != 5 || fact.Snapshot.Status != "ready"))
                        throw new InvalidDataException("Contents evidence association is invalid.");
                }
                versions[id] = version;
                if (!reader.IsDBNull(8))
                {
                    if (reader.GetValue(12) is not long size || size <= 0 || reader.GetValue(9) is not long ordinal)
                        throw new InvalidDataException("Invalid generated length or ordinal.");
                    items[id].Add((ordinal, new(reader.GetString(10), reader.GetString(11), size)));
                    if (items[id].Count > 5) throw new InvalidDataException("Too many bundle outputs.");
                }
            }
        }
        if (versions.Count != rows.Count) throw new InvalidDataException("Incomplete contents page.");
        foreach (var row in rows)
        {
            var list = items[row.RecordingId].OrderBy(i => i.Ordinal).ToArray();
            RecordingBundleContentsEvidence? evidence = null;
            if (versions[row.RecordingId] == 1)
            {
                evidence = new(list.Select(i => i.Item));
                if (list.Where((item, i) => item.Ordinal != i || item.Item.Name != evidence.Contents[i].Name).Any())
                    throw new InvalidDataException("Invalid contents ordering.");
            }
            else if (list.Length != 0) throw new InvalidDataException("Unregistered contents rows.");
            if (facts[row.RecordingId] is { } fact)
                facts[row.RecordingId] = fact with { Snapshot = fact.Snapshot with { ContentsEvidence = evidence } };
        }
    }

    internal static void Persist(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingBundleUpdate update)
    {
        var op = update.Operation;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ordinary_bundle_contents(recording_id,media_output_path,operation_id,bundle_path,evidence_version,contents_count)
            SELECT $id,$media,$op,$path,NULL,0 WHERE NOT EXISTS(SELECT 1 FROM ordinary_bundle_contents WHERE recording_id=$id);
            UPDATE ordinary_bundle_contents SET evidence_version=$version,contents_count=$count
            WHERE recording_id=$id AND media_output_path=$media AND operation_id=$op AND bundle_path=$path AND evidence_version IS NULL;
            """;
        command.Parameters.AddWithValue("$id", op.RecordingId); command.Parameters.AddWithValue("$media", op.MediaPath);
        command.Parameters.AddWithValue("$op", op.OperationId); command.Parameters.AddWithValue("$path", op.BundlePath);
        command.Parameters.AddWithValue("$version", update.Result.ContentsEvidence is null ? DBNull.Value : 1);
        command.Parameters.AddWithValue("$count", update.Result.ContentsEvidence is null ? 0 : 5);
        // A legacy generating operation may gain a marker only while settling its first result.
        if (command.ExecuteNonQuery() is not (1 or 2)) throw new InvalidDataException("Contents binding could not be committed.");
        if (update.Result.ContentsEvidence is not { } evidence) return;
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO ordinary_bundle_contents_items VALUES($id,$ordinal,$name,$type,$size);";
        for (var i = 0; i < evidence.Contents.Count; i++)
        {
            var item = evidence.Contents[i]; command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", op.RecordingId); command.Parameters.AddWithValue("$ordinal", i);
            command.Parameters.AddWithValue("$name", item.Name); command.Parameters.AddWithValue("$type", item.MediaType);
            command.Parameters.AddWithValue("$size", item.SizeBytes); command.ExecuteNonQuery();
        }
    }

    internal static void PrepareRecovery(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteSchemaV33.Validate(connection, transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM ordinary_recording_bundles b
                LEFT JOIN ordinary_bundle_contents c ON c.recording_id=b.recording_id
                CROSS JOIN ordinary_bundle_contents_boundary boundary
                WHERE b.status_code='generating' AND (
                  (c.recording_id IS NULL AND b.rowid>boundary.legacy_bundle_rowid) OR
                  EXISTS(SELECT 1 FROM ordinary_bundle_contents_items i WHERE i.recording_id=b.recording_id) OR
                  (c.recording_id IS NOT NULL AND (c.media_output_path IS NOT b.media_output_path OR
                    c.operation_id IS NOT b.operation_id OR c.bundle_path IS NOT b.bundle_path OR
                    c.evidence_version IS NOT NULL OR c.contents_count != 0))));
            """;
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidDataException("Recovery contents binding is invalid.");
        command.CommandText = "UPDATE ordinary_bundle_contents SET operation_id=NULL WHERE recording_id IN (SELECT recording_id FROM ordinary_recording_bundles WHERE status_code='generating');";
        command.ExecuteNonQuery();
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
