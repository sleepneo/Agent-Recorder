using Microsoft.Data.Sqlite;
namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV45
{
    internal const string Name = "schema_v45_bundle_attachment_references";
    internal const string IndexName = "idx_bundle_attachment_publication_identity";
    internal const string Definition = "CREATE INDEX idx_bundle_attachment_publication_identity ON ordinary_bundle_identity_items(volume_id,file_id);";
    internal const string Checksum = "3fb343df772451538bc107a53bb2230f1730f3c63e83494f58e2527fd9cebf88";
    internal static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { new SqliteMigrationDefinition(45, Name, Definition, Checksum) };
    internal static void Validate(SqliteConnection c, SqliteTransaction? t)
    {
        SqliteSchemaV35.Validate(c,t);
        if(SqliteSchemaV1.ComputeChecksum(Definition)!=Checksum)throw new InvalidDataException("Attachment reference checksum differs.");
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText="SELECT sql FROM sqlite_master WHERE type='index' AND name=$name AND tbl_name='ordinary_bundle_identity_items';";
        cmd.Parameters.AddWithValue("$name",IndexName);
        if(cmd.ExecuteScalar() is not string sql || SqliteSchemaV1.CanonicalizeDefinition(sql.TrimEnd(';')) != Definition.TrimEnd(';'))throw new InvalidDataException("Attachment reference index differs.");
        cmd.Parameters.Clear();cmd.CommandText="PRAGMA index_xinfo("+IndexName+");";
        using(var rd=cmd.ExecuteReader()) {
            var keys=new[]{"volume_id","file_id"};var n=0;
            while(rd.Read()) { if(rd.GetInt64(5)==0)continue;
                if(n>=keys.Length || rd.GetValue(2) is not string key || key!=keys[n++] || rd.GetInt64(3)!=0 || rd.GetValue(4) is not string collation || collation!="BINARY")throw new InvalidDataException("Attachment reference index keys differ."); }
            if(n!=keys.Length)throw new InvalidDataException("Attachment reference index keys missing.");
        }
        cmd.CommandText="SELECT name,definition_checksum FROM schema_migrations WHERE version=45;";
        using var receipt=cmd.ExecuteReader();
        if(!receipt.Read() || receipt.GetValue(0) is not string name || name!=Name || receipt.GetValue(1) is not string hash || hash!=Checksum || receipt.Read())throw new InvalidDataException("Attachment reference receipt differs.");
    }
}
