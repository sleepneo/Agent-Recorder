using System.Text.Json;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionLedgerSchemaTests
{
    [Fact]
    public async Task ExplicitLegacyProjectionExcludesV43ButDefaultSnapshotsStillObserveAllLedgerChanges()
    {
        using var f=new LocalRetentionDeletionFixture();var current=UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath);
        var legacy=UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath,schemaThrough:42);Assert.Contains(SqliteSchemaV43.Table,current);Assert.DoesNotContain(SqliteSchemaV43.Table,legacy);
        using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.NotEqual(current,UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath));Assert.Equal(legacy,UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath,schemaThrough:42));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task DuplicateApprovalOrAttemptIsRejectedByDatabaseConstraint(bool duplicateAttempt)
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        using var c=f.Writer();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO ordinary_deletion_operations("+string.Join(',',SqliteSchemaV43.BindingColumns)+",phase) SELECT "+string.Join(',',SqliteSchemaV43.BindingColumns.Select(x=>x==(duplicateAttempt?"approval_id":"operation_id")?"lower(hex(randomblob(16)))":x))+",'prepared_unknown' FROM ordinary_deletion_operations;";
        Assert.Equal(19,Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>cmd.ExecuteNonQuery()).SqliteErrorCode);Assert.Equal(1,f.Native.Calls);
    }
    [Fact]
    public void V42UpgradePreservesEveryOldValueDefinitionAndReceiptAndIsIdempotent()
    {
        using var f=new LocalRetentionDeletionFixture();DeletionLedgerFixture.Drop43(f.Db);
        var old=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true);
        using(var legacy=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.Db.Store.DatabasePath};Pooling=False;Mode=ReadOnly"))
        {legacy.Open();using var cmd=legacy.CreateCommand();cmd.CommandText="SELECT max(version) FROM schema_migrations;";Assert.Equal(42L,cmd.ExecuteScalar());}
        Assert.ThrowsAny<Exception>(()=>DeletionLedgerFixture.Reader(f).Page());
        Assert.Equal(old,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true));
        f.Db.Store.Initialize();using(var c=f.Db.Store.OpenReadOnlyConnection())
        {using var t=c.BeginTransaction();SqliteSchemaV43.Validate(c,t);}
        // Compare all legacy tables/schema directly, excluding ONLY newly added v43 objects/receipt.
        Assert.Equal(old,LegacyBytes(f));var once=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true);
        f.Db.Store.Initialize();new SqliteOperationalStore(f.Db.Store.DatabasePath).Initialize();Assert.Equal(once,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true));
        Assert.Equal(44,SqliteOperationalStore.CurrentSchemaVersion);Assert.Equal(f.Bytes,File.ReadAllBytes(f.Media));
    }
    private static string LegacyBytes(LocalRetentionDeletionFixture f)
    {
        // A separate owned v43 copy is downgraded, never the live source or parent evidence.
        var path=Path.Combine(f.Db.Root,"legacy-compare.db");
        using(var source=f.Db.Store.OpenReadOnlyConnection())
        using(var destination=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {destination.Open();source.BackupDatabase(destination);}
        using(var c=new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False;Mode=ReadWrite"))
        {c.Open();using var cmd=c.CreateCommand();cmd.CommandText="DROP TABLE ordinary_deletion_requests; DELETE FROM schema_migrations WHERE version=44; DROP TABLE ordinary_deletion_operations; DELETE FROM schema_migrations WHERE version=43;";cmd.ExecuteNonQuery();}
        return SqlitePublicationIdentityReferenceTests.OldBytes(path,true);
    }
    [Fact]
    public void ChecksumCatalogAndOnlyLocalFullPolicyAgree()
    {
        Assert.Equal(SqliteSchemaV43.MigrationChecksum,SqliteSchemaV1.ComputeChecksum(SqliteSchemaV43.Definition));
        Assert.Equal(44,SqliteSchemaCatalog.Migrations.Count);Assert.Equal(44,SqliteSchemaCatalog.Migrations.Last().Version);
        using var f=new LocalRetentionDeletionFixture();using var c=f.Db.Store.OpenConnection();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA synchronous;";Assert.Equal(1L,cmd.ExecuteScalar());
    }
    [Theory]
    [InlineData("DROP TABLE ordinary_deletion_operations;")]
    [InlineData("DROP INDEX idx_deletion_owner;")]
    [InlineData("DROP INDEX idx_deletion_approval;")]
    [InlineData("DROP TRIGGER deletion_transition;")]
    [InlineData("DROP TRIGGER deletion_no_delete;")]
    [InlineData("DELETE FROM schema_migrations WHERE version=43;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum=definition_checksum||char(0) WHERE version=43;")]
    public async Task BrokenSchemaNeverRepairsDuringRequestOrReadAndHasZeroNative(string sql)
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);f.Db.RawExecute(sql);
        var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);Assert.Equal(LocalDeletionProgress.NotAttempted,r.Progress);Assert.Equal(0,f.Native.Calls);
        Assert.ThrowsAny<Exception>(()=>DeletionLedgerFixture.Reader(f).Page());
        var damaged=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true);
        if(sql.StartsWith("DROP ",StringComparison.Ordinal))Assert.Throws<InvalidDataException>(()=>f.Db.Store.Initialize());
        else Assert.Throws<SqliteOperationalStoreException>(()=>f.Db.Store.Initialize());
        Assert.Equal(damaged,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true));
    }
    [Theory]
    [InlineData("UPDATE ordinary_deletion_operations SET output_path=output_path||'.mp4';")]
    [InlineData("UPDATE ordinary_deletion_operations SET approval_id=lower(hex(randomblob(16)));")]
    [InlineData("UPDATE ordinary_deletion_operations SET phase='prepared_unknown';")]
    [InlineData("DELETE FROM ordinary_deletion_operations;")]
    public async Task SettledRowsAndBindingsAreNotRewrittenOrCascaded(string sql)
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();var s=a.Request(f.Row.RecordingId);var p=await f.Approve(a,s);var r=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(p,f.Row.RecordingId),s);
        Assert.Equal(DeletionLedgerCommit.Committed,r.Settlement);using(var c=f.Writer())Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>RetentionGuardFixture.Write(c,sql));
        Assert.Equal(DeletionRecoveryState.Settled,DeletionLedgerFixture.Reader(f).Read(r.AttemptId)!.State);
    }
}
