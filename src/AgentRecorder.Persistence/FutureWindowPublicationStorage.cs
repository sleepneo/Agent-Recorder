using System.Buffers.Binary;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class FutureWindowPublicationStorage
{
    internal static void Write(SqliteConnection connection, SqliteTransaction transaction,
        FutureWindowAuthorizationRow row, PublicationReceipt? receipt)
    {
        var anchor = receipt?.Anchor;
        if (row.StatusCode != "completed" || row.RunStatus != "completed" ||
            anchor is not null && !Matches(anchor, row))
            throw new InvalidDataException("Future publication binding is invalid.");
        byte[]? volume = null, file = null;
        if (anchor is not null)
        {
            volume = new byte[8]; file = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(volume, anchor.Identity.VolumeSerialNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(file, anchor.Identity.IdLow);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(8), anchor.Identity.IdHigh);
        }
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO future_window_publication_anchors
                (authorization_id,run_id,output_path,output_size_bytes,observation_version,source,volume_id,file_id)
            VALUES($id,$run,$path,$size,$version,$source,$volume,$file)
            ON CONFLICT(authorization_id) DO UPDATE SET
                run_id=excluded.run_id,output_path=excluded.output_path,output_size_bytes=excluded.output_size_bytes,
                observation_version=excluded.observation_version,source=excluded.source,volume_id=excluded.volume_id,file_id=excluded.file_id;
            """;
        command.Parameters.AddWithValue("$id", row.AuthorizationId); command.Parameters.AddWithValue("$run", row.RunId!);
        command.Parameters.AddWithValue("$path", row.OutputPath!); command.Parameters.AddWithValue("$size", row.OutputSizeBytes!);
        command.Parameters.AddWithValue("$version", anchor is null ? DBNull.Value : PublicationIdentityAnchor.Version);
        command.Parameters.AddWithValue("$source", anchor is null ? DBNull.Value : PublicationIdentityAnchor.Source);
        command.Parameters.AddWithValue("$volume", (object?)volume ?? DBNull.Value); command.Parameters.AddWithValue("$file", (object?)file ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("Future publication was not saved.");
    }

    private static bool Matches(PublicationIdentityAnchor anchor, FutureWindowAuthorizationRow row) =>
        anchor.Valid && row.StatusCode == "completed" && row.RunStatus == "completed" &&
        row.OutputSizeBytes == anchor.Length && string.Equals(row.OutputPath, anchor.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.ToScope().OutputFilePath, anchor.Path, StringComparison.OrdinalIgnoreCase);

    // Selected rows, including non-completed states, are checked in the page's
    // existing snapshot. CROSS JOIN fixes indexed lookups instead of scanning.
    internal static Dictionary<string, PublicationIdentityAnchor?> Read(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<FutureWindowAuthorizationRow> rows,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? observe = null)
    {
        var legacy = SqliteSchemaV40.Validate(connection, transaction);
        if (rows.Count > 100) throw new InvalidDataException("Future publication page exceeds its bound.");
        var facts = rows.ToDictionary(r => r.AuthorizationId, StringComparer.Ordinal);
        var result = rows.ToDictionary(r => r.AuthorizationId, _ => (PublicationIdentityAnchor?)null, StringComparer.Ordinal);
        if (rows.Count == 0) return result;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "WITH requested(authorization_id) AS (VALUES " +
            string.Join(",", rows.Select((_, i) => "($id" + i + ")")) + ") " +
            "SELECT f.authorization_id,f.rowid,a.authorization_id,a.run_id,a.output_path,a.output_size_bytes,a.observation_version,a.source,a.volume_id,a.file_id," +
            "typeof(f.run_id),typeof(f.output_path),typeof(f.output_size_bytes),typeof(f.output_directory),typeof(f.output_file_name),typeof(f.proof_id),typeof(f.proof_nonce) " +
            "FROM requested CROSS JOIN future_window_authorizations f ON f.authorization_id=requested.authorization_id " +
            "LEFT JOIN future_window_publication_anchors a INDEXED BY " + SqliteSchemaV40.IndexName + " ON a.authorization_id=f.authorization_id LIMIT 101;";
        for (var i = 0; i < rows.Count; i++) command.Parameters.AddWithValue("$id" + i, rows[i].AuthorizationId);
        observe?.Invoke(connection, transaction, command.CommandText, command.Parameters.Cast<SqliteParameter>()
            .Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(0) is not string id || !facts.TryGetValue(id, out var row) || !seen.Add(id) || reader.GetValue(1) is not long sequence)
                throw new InvalidDataException("Future publication association is invalid.");
            var expectedTypes = new[] { row.RunId is null ? "null" : "text", row.OutputPath is null ? "null" : "text",
                row.OutputSizeBytes is null ? "null" : "integer", "text", "text", row.ProofId is null ? "null" : "text",
                row.ProofNonce is null ? "null" : "text" };
            if (expectedTypes.Where((type, i) => reader.GetString(10 + i) != type).Any())
                throw new InvalidDataException("Future publication parent field types are invalid.");
            if (reader.IsDBNull(2))
            {
                if (sequence > legacy) throw new InvalidDataException("Modern future publication marker is missing.");
                continue;
            }
            if (reader.GetValue(2) is not string anchorId || anchorId != id) throw new InvalidDataException("Future publication key is invalid.");
            if (row.StatusCode != "completed")
            {
                if (Enumerable.Range(3, 7).Any(i => !reader.IsDBNull(i))) throw new InvalidDataException("Orphan future publication evidence.");
                continue;
            }
            if (reader.GetValue(3) is not string run || run != row.RunId ||
                reader.GetValue(4) is not string path || !string.Equals(path, row.OutputPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(path, row.ToScope().OutputFilePath, StringComparison.OrdinalIgnoreCase) ||
                reader.GetValue(5) is not long size || size <= 0 || size != row.OutputSizeBytes)
                throw new InvalidDataException("Future publication run binding is invalid.");
            if (reader.IsDBNull(6))
            {
                if (Enumerable.Range(7, 3).Any(i => !reader.IsDBNull(i))) throw new InvalidDataException("Partial future publication evidence.");
                continue;
            }
            if (reader.GetValue(6) is not long version || version != PublicationIdentityAnchor.Version ||
                reader.GetValue(7) is not string source || source != PublicationIdentityAnchor.Source ||
                reader.GetValue(8) is not byte[] volume || volume.Length != 8 ||
                reader.GetValue(9) is not byte[] file || file.Length != 16)
                throw new InvalidDataException("Future publication format is invalid.");
            var anchor = new PublicationIdentityAnchor(path, size, new(BinaryPrimitives.ReadUInt64LittleEndian(volume),
                BinaryPrimitives.ReadUInt64LittleEndian(file), BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8))));
            if (!Matches(anchor, row)) throw new InvalidDataException("Future publication identity is invalid.");
            result[id] = anchor;
        }
        if (seen.Count != rows.Count) throw new InvalidDataException("Future publication row disappeared.");
        return result;
    }
}
