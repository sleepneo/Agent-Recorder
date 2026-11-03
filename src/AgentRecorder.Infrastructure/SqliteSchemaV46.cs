using Microsoft.Data.Sqlite;
namespace AgentRecorder.Infrastructure;

// Only persisted reference search entrances; no business data or old migration is rewritten.
internal static class SqliteSchemaV46
{
    internal const string Name = "schema_v46_bundle_persisted_references";
    internal static readonly (string Name, string Table, string Columns, string Sql)[] Indexes =
    {
        ("idx_v46_ordinary_recording_bundles_path", "ordinary_recording_bundles", "media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY", "CREATE INDEX idx_v46_ordinary_recording_bundles_path ON ordinary_recording_bundles(media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY)"),
        ("idx_v46_ordinary_bundle_contents_path", "ordinary_bundle_contents", "media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY", "CREATE INDEX idx_v46_ordinary_bundle_contents_path ON ordinary_bundle_contents(media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY)"),
        ("idx_v46_ordinary_bundle_identity_path", "ordinary_bundle_identity", "media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY", "CREATE INDEX idx_v46_ordinary_bundle_identity_path ON ordinary_bundle_identity(media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY)"),
        ("idx_v46_ordinary_media_hash_path", "ordinary_media_hash", "media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY", "CREATE INDEX idx_v46_ordinary_media_hash_path ON ordinary_media_hash(media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY)"),
        ("idx_v46_ordinary_bundle_attachment_hash_path", "ordinary_bundle_attachment_hash", "media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY", "CREATE INDEX idx_v46_ordinary_bundle_attachment_hash_path ON ordinary_bundle_attachment_hash(media_output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1,recording_id COLLATE BINARY)"),
        ("idx_v46_ordinary_bundle_identity_items_path", "ordinary_bundle_identity_items", "output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1", "CREATE INDEX idx_v46_ordinary_bundle_identity_items_path ON ordinary_bundle_identity_items(output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1)"),
        ("idx_v46_ordinary_bundle_attachment_hash_items_path", "ordinary_bundle_attachment_hash_items", "output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1", "CREATE INDEX idx_v46_ordinary_bundle_attachment_hash_items_path ON ordinary_bundle_attachment_hash_items(output_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1)"),
        ("idx_v46_ordinary_media_hash_identity", "ordinary_media_hash", "volume_id,file_id,recording_id COLLATE BINARY", "CREATE INDEX idx_v46_ordinary_media_hash_identity ON ordinary_media_hash(volume_id,file_id,recording_id COLLATE BINARY)"),
        ("idx_v46_ordinary_bundle_attachment_hash_items_identity", "ordinary_bundle_attachment_hash_items", "volume_id,file_id", "CREATE INDEX idx_v46_ordinary_bundle_attachment_hash_items_identity ON ordinary_bundle_attachment_hash_items(volume_id,file_id)"),
    };
    internal static readonly string Definition = string.Join(";\n", Indexes.Select(i => i.Sql)) + ";";
    internal const string Checksum = "0738f76e2af141ab528b733f95f4f112adab631742a97d35152b23e0e41f268c";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { new SqliteMigrationDefinition(46, Name, Definition, Checksum) };
    internal static void Validate(SqliteConnection c, SqliteTransaction? t)
    {
        SqliteSchemaV31.Validate(c,t); SqliteSchemaV33.Validate(c,t); SqliteSchemaV35.Validate(c,t);
        SqliteSchemaV36.Validate(c,t); SqliteSchemaV37.Validate(c,t);
        if (SqliteSchemaV1.ComputeChecksum(Definition) != Checksum) throw new InvalidDataException("Bundle references checksum differs.");
        using var cmd = c.CreateCommand(); cmd.Transaction = t;
        foreach (var index in Indexes)
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name AND tbl_name=$table;";
            cmd.Parameters.AddWithValue("$name", index.Name); cmd.Parameters.AddWithValue("$table", index.Table);
            if (cmd.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != index.Sql)
                throw new InvalidDataException("Bundle reference index differs.");
            cmd.Parameters.Clear(); cmd.CommandText = "PRAGMA index_xinfo(" + index.Name + ");";
            using var rd = cmd.ExecuteReader(); var keys = index.Columns.Split(','); var n = 0;
            while (rd.Read())
            {
                if (rd.GetInt64(5) == 0) continue;
                if (n >= keys.Length) throw new InvalidDataException("Unexpected bundle reference key.");
                var parts = keys[n++].Split(' '); var expectedCollation = parts.Length == 3 ? parts[2] : "BINARY";
                if (rd.GetValue(2) is not string key || key != parts[0] || rd.GetInt64(3) != 0 ||
                    rd.GetValue(4) is not string collation || collation != expectedCollation)
                    throw new InvalidDataException("Bundle reference key differs.");
            }
            if (n != keys.Length) throw new InvalidDataException("Bundle reference key missing.");
        }
        cmd.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=46;";
        using var receipt = cmd.ExecuteReader();
        if (!receipt.Read() || receipt.GetValue(0) is not string name || name != Name ||
            receipt.GetValue(1) is not string hash || hash != Checksum || receipt.Read())
            throw new InvalidDataException("Bundle reference receipt differs.");
    }
}
