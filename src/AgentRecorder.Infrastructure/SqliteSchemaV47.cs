using Microsoft.Data.Sqlite;
namespace AgentRecorder.Infrastructure;

// Only persisted reference search entrances; no business data or old migration is rewritten.
internal static class SqliteSchemaV47
{
    internal const string Name = "schema_v47_bundle_namespaces";
    internal static readonly (string Name, string Table, string Columns, string Sql)[] Indexes =
    {
        ("idx_v47_ordinary_recording_bundles_namespace", "ordinary_recording_bundles", "bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1", "CREATE INDEX idx_v47_ordinary_recording_bundles_namespace ON ordinary_recording_bundles(bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1)"),
        ("idx_v47_ordinary_bundle_contents_namespace", "ordinary_bundle_contents", "bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1", "CREATE INDEX idx_v47_ordinary_bundle_contents_namespace ON ordinary_bundle_contents(bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1)"),
        ("idx_v47_ordinary_bundle_identity_namespace", "ordinary_bundle_identity", "bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1", "CREATE INDEX idx_v47_ordinary_bundle_identity_namespace ON ordinary_bundle_identity(bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1)"),
        ("idx_v47_ordinary_media_hash_namespace", "ordinary_media_hash", "bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1", "CREATE INDEX idx_v47_ordinary_media_hash_namespace ON ordinary_media_hash(bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1)"),
        ("idx_v47_ordinary_bundle_attachment_hash_namespace", "ordinary_bundle_attachment_hash", "bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1", "CREATE INDEX idx_v47_ordinary_bundle_attachment_hash_namespace ON ordinary_bundle_attachment_hash(bundle_path COLLATE AGENTRECORDER_PATH_ORDINAL_IGNORE_CASE_V1)"),
    };
    internal static readonly string Definition = string.Join(";\n", Indexes.Select(i => i.Sql)) + ";";
    internal const string Checksum = "2353d44a84e7dcde1760b3a25be30276a302256e73d39bb06ed2ff04ffffff0c";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { new SqliteMigrationDefinition(47, Name, Definition, Checksum) };
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
        cmd.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=47;";
        using var receipt = cmd.ExecuteReader();
        if (!receipt.Read() || receipt.GetValue(0) is not string name || name != Name ||
            receipt.GetValue(1) is not string hash || hash != Checksum || receipt.Read())
            throw new InvalidDataException("Bundle reference receipt differs.");
    }
}
