using System.Net;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class DeletionRequestSchemaTests
{
    [Fact]
    public async Task GenuineV43WithSettledLedgerUpgradesWithoutChangingAnyOldTypedValueAndRepeatsIdempotently()
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();await f.Finish(id);
        Drop44(f.F.Db);var old=SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true);
        using(var c=new SqliteConnection($"Data Source={f.F.Db.Store.DatabasePath};Mode=ReadOnly;Pooling=False")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT max(version) FROM schema_migrations;";Assert.Equal(43L,cmd.ExecuteScalar());}
        Assert.ThrowsAny<Exception>(()=>DeletionLedgerFixture.Reader(f.F).Page());f.F.Db.Store.Initialize();
        var copy=Path.Combine(f.F.Db.Root,"legacy43-copy.db");using(var source=f.F.Db.Store.OpenReadOnlyConnection())using(var destination=new SqliteConnection($"Data Source={copy};Pooling=False")){destination.Open();source.BackupDatabase(destination);}
        using(var c=new SqliteConnection($"Data Source={copy};Mode=ReadWrite;Pooling=False")){c.Open();using var cmd=c.CreateCommand();cmd.CommandText="DROP TABLE ordinary_deletion_requests; DELETE FROM schema_migrations WHERE version=44;";cmd.ExecuteNonQuery();}
        Assert.Equal(old,SqlitePublicationIdentityReferenceTests.OldBytes(copy,true));var once=SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true);f.F.Db.Store.Initialize();Assert.Equal(once,SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true));
        Assert.Equal(SqliteSchemaV44.Checksum,SqliteSchemaV1.ComputeChecksum(SqliteSchemaV44.Definition));Assert.Equal(44,SqliteOperationalStore.CurrentSchemaVersion);Assert.Equal(f.F.Bytes,File.ReadAllBytes(f.F.Media));
    }
    internal static void Drop44(UnifiedHistoryTestDatabase d)=>d.RawExecute("DROP TABLE ordinary_deletion_requests; DELETE FROM schema_migrations WHERE version=44;");
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DatabaseEnforcesOwnerKeyAndApprovalUniqueness(bool approval)
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();await f.Finish(id);
        using var c=f.F.Writer();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO ordinary_deletion_requests SELECT lower(hex(randomblob(16))),owner_sid,owner_session,videos_root,host_id,"+(approval?"lower(hex(randomblob(32)))":"key_digest")+",request_digest,recording_id,created_ticks,phase,local_state,approval_id,root_retired,ledger_retired,preparation,settlement,updated_ticks FROM ordinary_deletion_requests;";
        Assert.Equal(19,Assert.Throws<SqliteException>(()=>cmd.ExecuteNonQuery()).SqliteErrorCode);Assert.Equal(1,f.F.Native.Calls);
    }
    [Theory]
    [InlineData("UPDATE ordinary_deletion_requests SET recording_id='different';")]
    [InlineData("UPDATE ordinary_deletion_requests SET approval_id=lower(hex(randomblob(16)));")]
    [InlineData("UPDATE ordinary_deletion_requests SET phase='awaiting_confirmation';")]
    [InlineData("DELETE FROM ordinary_deletion_requests;")]
    public async Task FinishedBindingsAndTransitionsAreImmutable(string sql)
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();await f.Finish(id);using var c=f.F.Writer();
        Assert.Throws<SqliteException>(()=>RetentionGuardFixture.Write(c,sql));Assert.Equal(id,(await f.Read(id)).GetProperty("deletion_request_id").GetString());
    }
    [Theory]
    [InlineData("DROP TABLE ordinary_deletion_requests;")][InlineData("DROP INDEX idx_deletion_request_key;")][InlineData("DROP INDEX idx_deletion_request_approval;")]
    [InlineData("DROP TRIGGER deletion_request_transition;")][InlineData("DROP TRIGGER deletion_request_no_delete;")][InlineData("DELETE FROM schema_migrations WHERE version=44;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum=definition_checksum||char(0) WHERE version=44;")]
    public async Task BrokenV44IsRejectedWithoutRepairOrUiAndOldDataStayByteEqual(string sql)
    {
        using var f=new DeletionRequestFixture();f.F.Db.RawExecute(sql);var before=SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true);
        var r=await f.Post();Assert.Equal(HttpStatusCode.ServiceUnavailable,r.Status);Assert.Equal(0,f.F.Ui.Begins);Assert.Equal(0,f.F.Native.Calls);
        Assert.Equal(before,SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true));Assert.ThrowsAny<Exception>(()=>f.F.Db.Store.Initialize());Assert.Equal(before,SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath,true));
    }
    [Theory][InlineData("volume_id",20_000_000)][InlineData("file_id",20_000_000)][InlineData("volume_id",7)][InlineData("file_id",15)]
    public async Task ActualLedgerSqlRejectsOversizedBlobBeforeManagedMaterializationAndPublicReadFailsClosed(string column,int size)
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();var done=await f.Finish(id);var op=done.GetProperty("operation_id").GetString()!;
        DeletionLedgerFixture.CorruptBinding(f.F,$"UPDATE ordinary_deletion_operations SET {column}=zeroblob({size});");
        using var c=f.F.Db.Store.OpenReadOnlyConnection();using var t=c.BeginTransaction(deferred:true);var allocated=GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(()=>DeletionLedger.ReadRow(c,t,op));var delta=GC.GetAllocatedBytesForCurrentThread()-allocated;Assert.True(delta<4_000_000,$"Managed materialization allocated {delta} for invalid blob {size}.");
        using var response=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode);Assert.Equal(1,f.F.Native.Calls);Assert.Equal(1,f.F.Ui.Begins);
    }
    [Fact]
    public async Task LegalFull128BitTupleRemainsExactAndAnotherOwnerGetsSame404AsMissing()
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();var done=await f.Finish(id);var entry=DeletionLedgerFixture.Reader(f.F).Read(done.GetProperty("operation_id").GetString()!)!;Assert.Equal(f.F.Anchor.Identity,entry.Binding.Anchor.Identity);Assert.Equal(789UL,entry.Binding.Anchor.Identity.IdHigh);
        using var foreignAuthority=new LocalRetentionApproval(f.F.Engine,f.F.Db.Store,f.F.Db.Ordinary,"different-owner",UnifiedHistoryTestDatabase.Session,f.F.Videos,f.F.Ui);
        using var foreign=new RetentionDeletionCoordinator(f.F.Db.Store,"different-owner",UnifiedHistoryTestDatabase.Session,f.F.Videos,foreignAuthority,queryAdmission:new());
        Assert.Null(await foreign.ReadAsync(id));Assert.Null(await f.Coordinator.ReadAsync(new string('f',32)));Assert.Equal(1,f.F.Native.Calls);
    }
    [Theory][InlineData("approval_id","zeroblob(32)")][InlineData("recording_id","recording_id||char(0)||'hidden'")][InlineData("request_digest","upper(request_digest)")]
    public async Task DamagedRequestAssociationAndFullTextAreNeverConvertedToAnExecutableRequest(string column,string expression)
    {
        using var f=new DeletionRequestFixture();var id=await f.Create();await f.Approve();await f.Finish(id);
        // STRICT rejects BLOB types directly; a text coercion still fails full type/domain decoding.
        if(expression.StartsWith("zeroblob"))expression="CAST(zeroblob(32) AS TEXT)";
        f.F.Db.RawExecute("DROP TRIGGER deletion_request_transition; PRAGMA ignore_check_constraints=ON; UPDATE ordinary_deletion_requests SET "+column+"="+expression+"; "+SqliteSchemaV44.Objects.Single(o=>o.Name=="deletion_request_transition").Sql+";");
        using var r=await f.Client.GetAsync(DeletionRequestFixture.Endpoint+"/"+id);Assert.Equal(HttpStatusCode.ServiceUnavailable,r.StatusCode);Assert.Equal(1,f.F.Native.Calls);
    }
}
