using System.Net;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using static AgentRecorder.Tests.BatchDeletion388Fixture;

namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class BatchDeletion388SchemaTests
{
    [Fact]
    public void IncrementalV48PreservesV1Through47AndBusinessRowsOnRepeatedInitialization()
    {
        using var f=new BatchDeletion388Fixture();
        f.F.Db.RawExecute("DROP TABLE ordinary_deletion_host_rejections; DELETE FROM schema_migrations WHERE version=51; DROP TABLE ordinary_deletion_batch_policy_children; DROP TABLE ordinary_deletion_batch_policies; DELETE FROM schema_migrations WHERE version=50; DROP TABLE ordinary_deletion_operation_policies; DROP TABLE ordinary_deletion_request_policies; DELETE FROM schema_migrations WHERE version=49; DROP TABLE ordinary_deletion_batch_items; DROP TABLE ordinary_deletion_batches; DELETE FROM schema_migrations WHERE version=48;");
        var before=SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true,47);f.F.Db.Store.Initialize();f.F.Db.Store.Initialize();
        Assert.Equal(before,SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true,47));Assert.Equal(51,SqliteOperationalStore.CurrentSchemaVersion);
        Assert.Equal(SqliteSchemaV48.Checksum,SqliteSchemaV1.ComputeChecksum(SqliteSchemaV48.Definition));Assert.Equal(1,f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=48;"));f.PreserveBytes();
    }
    [Theory][InlineData("DROP TABLE ordinary_deletion_batch_items;")][InlineData("DROP INDEX idx_deletion_batch_key;")][InlineData("DROP TRIGGER deletion_batch_transition;")][InlineData("DELETE FROM schema_migrations WHERE version=48;")][InlineData("UPDATE schema_migrations SET definition_checksum='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' WHERE version=48;")]
    public async Task MissingOrDamagedSchemaNeverDispatchesOrRepairsOnReads(string sql)
    {
        using var f=new BatchDeletion388Fixture();f.F.Db.RawExecute(sql);var before=SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await f.Post()).Status);Assert.Equal(0,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);Assert.Equal(before,SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true));
    }
    [Theory][InlineData("UPDATE ordinary_deletion_batches SET request_digest=upper(request_digest);")][InlineData("UPDATE ordinary_deletion_batch_items SET recording_id=recording_id||char(0)||'hidden';")]
    [InlineData("UPDATE ordinary_deletion_batch_items SET ordinal=4 WHERE ordinal=1;")][InlineData("DELETE FROM ordinary_deletion_batch_items WHERE ordinal=1;")]
    [InlineData("UPDATE ordinary_deletion_batch_items SET child_key='ffffffffffffffffffffffffffffffff' WHERE ordinal=1;")]
    [InlineData("UPDATE ordinary_deletion_batch_items SET child_request_id='ffffffffffffffffffffffffffffffff' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_deletion_batch_items SET child_key='ffffffffffffffffffffffffffffffff' WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_deletion_batches SET item_count=1.5;")]
    public async Task DamagedTypedFactsAndPartialAssociationsFailClosedWithoutAnotherUi(string sql)
    {
        using var f=new BatchDeletion388Fixture();var id=await f.Create();var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);await f.Finish(id);
        // STRICT itself rejects fractional INTEGER; preserve that assertion.
        if(sql.Contains("1.5")){Assert.Throws<SqliteException>(()=>f.F.Db.Execute(sql));return;}
        f.F.Db.Corrupt(sql);var before=f.F.Db.Snapshot();
        await Assert.ThrowsAsync<DeletionRequestUnavailableException>(()=>f.Batch.ReadAsync(id));Assert.Equal(HttpStatusCode.ServiceUnavailable,(await f.Post()).Status);
        Assert.Equal(before,f.F.Db.Snapshot());Assert.Equal(1,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
    }
    [Theory][InlineData("UPDATE ordinary_deletion_batch_items SET child_key=lower(hex(randomblob(16))) WHERE ordinal=0;")]
    [InlineData("UPDATE ordinary_deletion_batch_items SET recording_id='other' WHERE ordinal=0;")][InlineData("DELETE FROM ordinary_deletion_batches;")][InlineData("UPDATE ordinary_deletion_batches SET phase='active';")]
    public async Task FrozenBindingsAndFinishedParentAreDatabaseImmutable(string sql)
    {
        using var f=new BatchDeletion388Fixture();var id=await f.Create();var p=await f.Prompts.Next();p.Decide(LocalRetentionUiChoice.Reject);await f.Finish(id);
        Assert.Throws<SqliteException>(()=>f.F.Db.Execute(sql));Assert.Equal(1,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
    }
    [Fact]
    public async Task DuplicateHostKeysAndOutOfOrderItemReservationsAreRejectedByDatabase()
    {
        using var f=new BatchDeletion388Fixture();var id=await f.Create();var p=await f.Prompts.Next();
        Assert.Throws<SqliteException>(()=>f.F.Db.Execute("UPDATE ordinary_deletion_batch_items SET phase='dispatch_reserved' WHERE ordinal=1;"));
        Assert.Equal(19,Assert.Throws<SqliteException>(()=>f.F.Db.Execute("INSERT INTO ordinary_deletion_batches SELECT lower(hex(randomblob(16))),owner_sid,owner_session,videos_root,host_id,key_digest,request_digest,binding_digest,item_count,created_ticks,expires_ticks,phase,stop_reason,updated_ticks FROM ordinary_deletion_batches;")).SqliteErrorCode);
        p.Decide(LocalRetentionUiChoice.Reject);await f.Finish(id);Assert.Equal(0,f.Native.Calls);
    }
    [Fact]
    public async Task CaseDistinctRecordingIdsStaySeparateObjectsAndOrderIsNeverFolded()
    {
        using var f=new BatchDeletion388Fixture();var result=await f.Batch.RequestAsync(new[]{"Case","case"},"case-test");Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(6)));
        var final=await f.Batch.ReadAsync(result.BatchDeletionRequestId);Assert.Equal(new[]{"Case","case"},final!.Items.Select(i=>i.RecordingId));Assert.Equal("not_requested",final.Items[1].State);Assert.Equal(0,f.Prompts.Begins);Assert.Equal(0,f.Native.Calls);
    }
}
