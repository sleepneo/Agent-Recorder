using System.Data;
using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

public sealed class SqlitePublicationIdentityReferenceTests(ITestOutputHelper output)
{
    internal static IEnumerable<object[]> Faults()
    {
        foreach (var index in SqliteSchemaV42.Indexes)
        {
            yield return new object[] { "DROP INDEX " + index.Name + ";" };
            yield return new object[] { "DROP INDEX " + index.Name + "; CREATE INDEX " + index.Name + " ON " + index.Table + "(file_id,volume_id);" };
            yield return new object[] { "DROP INDEX " + index.Name + "; CREATE INDEX " + index.Name + " ON " + index.Table + "(volume_id COLLATE NOCASE,file_id," + index.Owner + ");" };
            yield return new object[] { "DROP INDEX " + index.Name + "; CREATE INDEX " + index.Name + " ON " + index.Table + "(volume_id,file_id," + index.Owner + " COLLATE BINARY) WHERE volume_id IS NOT NULL;" };
        }
        yield return new object[] { "DELETE FROM schema_migrations WHERE version=42;" };
        yield return new object[] { "UPDATE schema_migrations SET name='SECRET_RECEIPT' WHERE version=42;" };
        yield return new object[] { "UPDATE schema_migrations SET definition_checksum=definition_checksum||char(0) WHERE version=42;" };
        yield return new object[] { "UPDATE schema_migrations SET name=CAST(name AS BLOB) WHERE version=42;" };
        foreach (var table in new[] { "ordinary_publication_anchors", "future_window_publication_anchors", "plan_media_publication_anchors" })
            yield return new object[] { "DROP TABLE " + table + ";" };
    }
    public static IEnumerable<object[]> SchemaFaults => Faults();

    [Theory, MemberData(nameof(SchemaFaults))]
    public void BrokenSchemaFailsReadWithoutRepairAndInitializeRejectsIt(string sql)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); f.Seed("a"); f.Seed("b"); f.Db.Ordinary.Read("b");
        f.Db.RawExecute(sql); var before = OldBytes(f.Db.Store.DatabasePath, include42: true);
        Assert.Throws<RetentionPreviewUnavailableException>(() => f.Page()); RetentionRuntimeOutputTests.NoProbes(f);
        Assert.Equal(before, OldBytes(f.Db.Store.DatabasePath, include42: true));
        // Migration-history faults retain the catalog's existing exception;
        // exact schema/typed-receipt faults retain their validator exception.
        if (sql.StartsWith("DELETE FROM schema_migrations") || sql.StartsWith("UPDATE schema_migrations") && !sql.Contains("CAST"))
            Assert.Throws<SqliteOperationalStoreException>(() => f.Db.Store.Initialize());
        else Assert.Throws<InvalidDataException>(() => f.Db.Store.Initialize());
        Assert.Equal(before, OldBytes(f.Db.Store.DatabasePath, include42: true));
    }

    [Fact]
    public void V41ReadonlyIsUnavailableUntilNormalInitializePreservingAllOriginalBytes()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate"); f.Db.SeedFuture("null_marker");
        f.Db.Ordinary.Read(row.RecordingId); f.Db.DropPublicationReferenceMigration();
        var old = OldBytes(f.Db.Store.DatabasePath);
        Assert.Throws<RetentionPreviewUnavailableException>(() => f.Page()); RetentionRuntimeOutputTests.NoProbes(f);
        Assert.Equal(old, OldBytes(f.Db.Store.DatabasePath));
        using (var legacy = new SqliteConnection($"Data Source={f.Db.Store.DatabasePath};Pooling=False;Mode=ReadOnly"))
        {
            legacy.Open(); using var cmd = legacy.CreateCommand(); cmd.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(41L, cmd.ExecuteScalar());
        }
        f.Db.Store.Initialize(); Assert.Equal(old, OldBytes(f.Db.Store.DatabasePath));
        var once = OldBytes(f.Db.Store.DatabasePath, include42: true);
        new SqliteOperationalStore(f.Db.Store.DatabasePath).Initialize(); f.Db.Store.Initialize();
        Assert.Equal(once, OldBytes(f.Db.Store.DatabasePath, include42: true));
        Assert.Equal(44, SqliteOperationalStore.CurrentSchemaVersion); // Current Initialize installs the separate v43/v44 additions.
        Assert.Equal(1, f.Db.Scalar("SELECT count(*) FROM future_window_publication_anchors WHERE authorization_id='null_marker' AND volume_id IS NULL AND file_id IS NULL;"));
        using var c = f.Db.Store.OpenReadOnlyConnection(); using var t = c.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        SqliteSchemaV42.Validate(c, t); t.Commit();
        Assert.Equal("age_rule_match", Assert.Single(f.Page().Items).ReasonCode);
        output.WriteLine("V41->V42: all old rows (type + full BLOB hex), definitions/triggers/indexes and receipts byte-equal; reopen/idempotence verified; NULL marker unchanged.");
    }

    [Fact]
    public void MigrationContainsOnlyThreeIndexesAndOneNormalReceipt()
    {
        var migration = Assert.Single(SqliteSchemaV42.Migrations); Assert.Equal(42, migration.Version);
        Assert.Equal(SqliteSchemaV1.ComputeChecksum(migration.Definition), migration.Checksum);
        Assert.Equal(3, migration.Definition.Split(';', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.All(migration.Definition.Split(';', StringSplitOptions.RemoveEmptyEntries), sql => Assert.StartsWith("CREATE INDEX ", sql.Trim()));
        Assert.Equal(41, SqliteSchemaCatalog.Migrations.Count(m => m.Version <= 41));
    }

    [Fact]
    public void EvenEmptyUnanchoredPageRequiresV42AndNoRelationshipIsInvented()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); f.Db.DropPublicationReferenceMigration();
        Assert.Throws<RetentionPreviewUnavailableException>(() => f.Page()); RetentionRuntimeOutputTests.NoProbes(f);
        f.Db.Store.Initialize(); var row = f.Db.Recording("legacy", completed: true) with { OutputPath = Path.Combine(f.Videos, "legacy.mp4"), BundleRegistration = new(null) };
        f.Db.Ordinary.Register(row); f.Db.SeedFuture("unknown_marker"); var calls = 0;
        f.Db.Query.RetentionIdentityCommandForTests = (_, _, _, _) => calls++;
        var evidence = Assert.Single(f.Db.Query.GetOrdinaryEvidencePage(UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, 20, f.Before, null).Items);
        Assert.Null(evidence.PublicationAnchor); Assert.Null(evidence.PublicationIdentityReference); Assert.Equal(0, calls);
        Assert.Equal("publication_identity_missing", Assert.Single(f.Page().Items).ReasonCode); RetentionRuntimeOutputTests.NoProbes(f);
    }

    internal static string OldBytes(string path, bool include42 = false)
    {
        using var c = new SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadOnly"); c.Open();
        var lines = new List<string>(); var tables = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                // The v41 comparison excludes only subsequent v42/v43/v44 objects.
                // Task360 separately compares EVERY v42 value/definition across v43.
                if (!include42 && (rd.GetString(2)==SqliteSchemaV44.Table || rd.GetString(2)==SqliteSchemaV43.Table || SqliteSchemaV43.Objects.Any(o=>o.Name==rd.GetString(1)))) continue;
                if (rd.GetString(0) == "table") tables.Add(rd.GetString(1));
                if (!include42 && SqliteSchemaV42.Indexes.Any(i => i.Name == rd.GetString(1))) continue;
                lines.Add(JsonSerializer.Serialize(Enumerable.Range(0, rd.FieldCount).Select(i => rd.GetValue(i))));
            }
        }
        foreach (var table in tables)
        {
            using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT rowid,* FROM \"" + table + "\"" +
                (table == "schema_migrations" && !include42 ? " WHERE version<=41" : "") + " ORDER BY rowid;";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) lines.Add(table + ":" + JsonSerializer.Serialize(Enumerable.Range(0, rd.FieldCount)
                .Select(i => rd.GetValue(i) is byte[] blob ? "BLOB:" + Convert.ToHexString(blob) : rd.GetValue(i).GetType().Name + ":" + rd.GetValue(i))));
        }
        return string.Join("\n", lines);
    }
}
