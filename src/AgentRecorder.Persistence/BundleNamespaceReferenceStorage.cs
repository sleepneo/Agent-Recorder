using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

// Persisted lexical scopes. All batches belong to the caller's original snapshot.
internal static class BundleNamespaceReferenceStorage
{
    internal const int MaxCandidates = 100;
    internal const int MaxKeysPerCandidate = 1 + 2 * RetentionPreviewQueryService.MaxPathComponents;
    internal const int MaxTotalKeys = MaxCandidates * MaxKeysPerCandidate;
    internal const int KeysPerBatch = 256;
    internal const int MaxParametersPerBatch = 2 * KeysPerBatch;
    internal const int MaxBatches = (MaxTotalKeys + KeysPerBatch - 1) / KeysPerBatch;
    internal const int MaxLegacyOwnersPerKey = 8;

    private static readonly string LegacyMp4Scope = $"""
        CASE WHEN substr(requested.directory_path,-7) COLLATE {SqliteOutputPathCollation.Name} = '.bundle'
        THEN (
            SELECT COUNT(*) > {MaxLegacyOwnersPerKey} OR COALESCE(MAX(CASE
                WHEN owner.status_code IN ('pending_confirmation','preparing','countdown','recording','finalizing',
                    'failed','cancelled','rejected','expired','interrupted_unknown') THEN 0
                WHEN owner.status_code = 'completed' AND typeof(owner.recording_id) = 'text' AND EXISTS (
                    SELECT 1 FROM ordinary_recording_bundles b
                    WHERE b.recording_id = owner.recording_id
                      AND b.media_output_path COLLATE BINARY = owner.output_path
                      AND b.status_code = 'not_applicable'
                      AND b.operation_id IS NULL AND b.bundle_path IS NULL AND b.error_code IS NULL
                ) THEN 0
                ELSE 1 END), 0)
            FROM (
                SELECT recording_id, status_code, output_path FROM ordinary_recording_history
                INDEXED BY idx_ordinary_recording_history_shared_output_path
                WHERE output_path COLLATE {SqliteOutputPathCollation.Name} =
                    substr(requested.directory_path,1,length(requested.directory_path)-7) || '.mp4'
                LIMIT {MaxLegacyOwnersPerKey + 1}
            ) owner
        ) ELSE 0 END
        """;

    internal static IReadOnlyList<string>? AncestorKeys(string? path)
    {
        try
        {
            if (path is null || path.Length < 3 || path.Length > RetentionPreviewQueryService.MaxPathLength ||
                !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('/') || path.Contains('\0')) return null;
            UnifiedHistoryQueryService.ValidatePath(path);
            var root = path[..3];
            if (path.Length == 3) return new[] { root };
            var parts = path[3..].Split('\\');
            if (parts.Length > RetentionPreviewQueryService.MaxPathComponents ||
                parts.Any(p => !RetentionPreviewQueryService.ValidComponent(p))) return null;
            var keys = new List<string> { root }; var current = root;
            foreach (var part in parts)
            {
                current += (current.Length == 3 ? "" : "\\") + part;
                keys.Add(current); keys.Add(current + "\\");
            }
            if (keys.Count > MaxKeysPerCandidate) throw new InvalidDataException("Namespace key bound differs.");
            return keys;
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or NotSupportedException) { return null; }
    }

    internal static IReadOnlyDictionary<string, bool> Read(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyDictionary<string, string?> candidates,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? inspect = null,
        Func<IReadOnlyList<(object CandidateId, object Flag)>, IReadOnlyList<(object CandidateId, object Flag)>>? transform = null)
    {
        if (candidates.Count > MaxCandidates) throw new InvalidDataException("Namespace candidate bound exceeded.");
        SqliteSchemaV47.Validate(connection, transaction);
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var all = new List<(string Id, string Key)>();
        foreach (var candidate in candidates)
        {
            var keys = AncestorKeys(candidate.Value);
            // Never report an unexamined path as unreferenced. Preview's earlier
            // lexical checks retain their reason; a raw Guard stays conservative.
            result.Add(candidate.Key, keys is null);
            if (keys is not null) all.AddRange(keys.Select(k => (candidate.Key, k)));
        }
        if (all.Count > MaxTotalKeys) throw new InvalidDataException("Namespace total key bound exceeded.");
        for (var offset = 0; offset < all.Count; offset += KeysPerBatch)
        {
            var batch = all.Skip(offset).Take(KeysPerBatch).ToArray();
            var expected = batch.Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            // Invert DerivePath only for historical MP4s. The suffix test also
            // excludes trailing-separator keys. Limit index hits before testing
            // registration so even many matching media-only rows fail closed.
            command.CommandText = "WITH requested(recording_id,directory_path) AS (VALUES " +
                string.Join(",", batch.Select((_, i) => "($id" + i + ",$dir" + i + ")")) + ") " +
                "SELECT requested.recording_id, MAX(" + string.Join(" OR ", SqliteSchemaV47.Indexes.Select(index =>
                    "EXISTS(SELECT 1 FROM " + index.Table + " scope INDEXED BY " + index.Name +
                    " WHERE scope.bundle_path COLLATE " + SqliteOutputPathCollation.Name + "=requested.directory_path)")) +
                " OR " + LegacyMp4Scope + ") FROM requested GROUP BY requested.recording_id COLLATE BINARY;";
            for (var i = 0; i < batch.Length; i++)
            {
                command.Parameters.AddWithValue("$id" + i, batch[i].Id);
                command.Parameters.AddWithValue("$dir" + i, batch[i].Key);
            }
            inspect?.Invoke(connection, transaction, command.CommandText, command.Parameters.Cast<SqliteParameter>()
                .Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
            var rows = new List<(object CandidateId, object Flag)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rows.Add((reader.GetValue(0), reader.GetValue(1)));
            var observed = transform is null ? rows : transform(rows);
            if (observed is null || observed.Count != expected.Count) throw new InvalidDataException("Namespace results missing.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in observed)
            {
                if (row.CandidateId is not string id || !expected.Contains(id) || !seen.Add(id) ||
                    row.Flag is not long flag || flag is not (0 or 1)) throw new InvalidDataException("Namespace result differs.");
                result[id] |= flag == 1;
            }
        }
        return result;
    }
}
