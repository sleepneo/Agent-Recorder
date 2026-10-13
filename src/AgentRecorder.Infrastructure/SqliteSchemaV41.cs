using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV41
{
    internal const string MigrationName = "schema_v41_plan_media_publication_identity";
    internal const string IndexName = "idx_plan_media_publication_run";
    internal const string Boundary = """
        CREATE TABLE plan_media_publication_boundary (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
            legacy_run_rowid INTEGER NOT NULL CHECK (legacy_run_rowid >= 0)
        )
        """;
    internal const string Anchors = """
        CREATE TABLE plan_media_publication_anchors (
            run_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY,
            plan_id TEXT, occurrence_id TEXT, owner_kind TEXT, owner_id TEXT,
            frozen_output_path TEXT, output_path TEXT, output_size_bytes INTEGER,
            observation_version INTEGER, source TEXT, volume_id BLOB, file_id BLOB, receipt_size_bytes INTEGER,
            CHECK ((plan_id IS NULL AND occurrence_id IS NULL AND owner_kind IS NULL AND owner_id IS NULL AND
                frozen_output_path IS NULL AND output_path IS NULL AND output_size_bytes IS NULL) OR
                (typeof(plan_id) = 'text' AND length(plan_id) BETWEEN 1 AND 128 AND instr(plan_id,char(0)) = 0 AND
                 typeof(occurrence_id) = 'text' AND length(occurrence_id) BETWEEN 1 AND 128 AND instr(occurrence_id,char(0)) = 0 AND
                 typeof(owner_kind) = 'text' AND owner_kind IN ('standing','recurring','required_once') AND
                 typeof(owner_id) = 'text' AND length(owner_id) BETWEEN 1 AND 128 AND instr(owner_id,char(0)) = 0 AND
                 typeof(frozen_output_path) = 'text' AND length(frozen_output_path) BETWEEN 4 AND 4096 AND instr(frozen_output_path,char(0)) = 0 AND
                 typeof(output_path) = 'text' AND length(output_path) BETWEEN 4 AND 4096 AND instr(output_path,char(0)) = 0 AND
                 typeof(output_size_bytes) = 'integer' AND output_size_bytes > 0)),
            CHECK ((observation_version IS NULL AND source IS NULL AND volume_id IS NULL AND file_id IS NULL AND receipt_size_bytes IS NULL) OR
                (output_size_bytes IS NOT NULL AND typeof(receipt_size_bytes) = 'integer' AND receipt_size_bytes = output_size_bytes AND typeof(observation_version) = 'integer' AND observation_version = 1 AND
                 typeof(source) = 'text' AND source = 'staging_copy_handle/v1' AND
                 typeof(volume_id) = 'blob' AND length(volume_id) = 8 AND volume_id != zeroblob(8) AND
                 typeof(file_id) = 'blob' AND length(file_id) = 16 AND file_id != zeroblob(16))),
            FOREIGN KEY (run_id) REFERENCES recording_runs(id),
            FOREIGN KEY (plan_id) REFERENCES plans(id),
            FOREIGN KEY (occurrence_id,plan_id) REFERENCES plan_occurrences(id,plan_id)
        )
        """;
    internal const string Index = "CREATE INDEX idx_plan_media_publication_run ON plan_media_publication_anchors(run_id COLLATE BINARY)";
    internal const string Trigger = """
        CREATE TRIGGER plan_media_publication_initial AFTER INSERT ON recording_runs BEGIN
            INSERT INTO plan_media_publication_anchors(run_id) VALUES(NEW.id);
        END
        """;
    internal const string Definition = Boundary + ";\nINSERT INTO plan_media_publication_boundary SELECT 1,COALESCE(MAX(rowid),0) FROM recording_runs;\n" +
        Anchors + ";\n" + Index + ";\n" + Trigger + ";";
    internal const string MigrationChecksum = "456eb0beec7d0ab8c398d5f698e5531aaf4e5b538346df9a99d459bde41118cb";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] {
        new SqliteMigrationDefinition(41, MigrationName, Definition, MigrationChecksum) };

    internal static long Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (SqliteSchemaV1.ComputeChecksum(Definition) != MigrationChecksum) throw new InvalidDataException("Plan publication checksum differs.");
        var objects = new[] { ("table", "plan_media_publication_boundary", Boundary), ("table", "plan_media_publication_anchors", Anchors),
            ("index", IndexName, Index), ("trigger", "plan_media_publication_initial", Trigger) };
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        foreach (var (type,name,expected) in objects)
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name;";
            cmd.Parameters.AddWithValue("$type",type); cmd.Parameters.AddWithValue("$name",name);
            if (cmd.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != SqliteSchemaV1.CanonicalizeDefinition(expected))
                throw new InvalidDataException("Plan publication schema is invalid.");
            cmd.Parameters.Clear();
        }
        cmd.CommandText = "SELECT name,definition_checksum FROM schema_migrations WHERE version=41;";
        using (var reader = cmd.ExecuteReader())
            if (!reader.Read() || reader.GetValue(0) is not string name || name != MigrationName ||
                reader.GetValue(1) is not string checksum || checksum != MigrationChecksum || reader.Read())
                throw new InvalidDataException("Plan publication migration receipt is invalid.");
        cmd.CommandText = "SELECT singleton,legacy_run_rowid FROM plan_media_publication_boundary;";
        long legacy;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || reader.GetValue(0) is not long singleton || singleton != 1 ||
                reader.GetValue(1) is not long value || value < 0 || reader.Read()) throw new InvalidDataException("Plan publication boundary is invalid.");
            legacy = value;
        }
        cmd.CommandText = "SELECT COALESCE(MAX(rowid),0) FROM recording_runs;";
        if (cmd.ExecuteScalar() is not long maximum || legacy > maximum) throw new InvalidDataException("Plan publication boundary is invalid.");
        return legacy;
    }
}
