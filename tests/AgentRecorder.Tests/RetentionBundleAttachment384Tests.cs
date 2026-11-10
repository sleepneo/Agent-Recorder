using System.Buffers.Binary;
using AgentRecorder.Capture;
using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Data.Sqlite;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public class RetentionBundleAttachment384Tests(ITestOutputHelper output)
{
    internal static void Insert(RetentionPreviewQueryTests.Fixture f, PublicationIdentityAnchor anchor,
        string name = "metadata.json", string owner = "SECRET_FOREIGN")
        => InsertDb(f.Db,anchor,name,owner);
    internal static void InsertDb(UnifiedHistoryTestDatabase db, PublicationIdentityAnchor anchor,string name="metadata.json",string owner="SECRET_FOREIGN")
    {
        var v = new byte[8];var id = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(v,anchor.Identity.VolumeSerialNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(id,anchor.Identity.IdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8),anchor.Identity.IdHigh);
        // Synthetic orphan association, not a duplicate publication of playable media.
        db.Corrupt("INSERT INTO ordinary_bundle_identity_items VALUES($owner,0,$name,'C:\\SECRET\\foreign',999,1,'bundle_copy_handle/v1',$v,$id);",
            ("$owner",owner),("$name",name),("$v",v),("$id",id));
    }
    [Theory]
    [InlineData("metadata.json")][InlineData("thumbnail.jpg")][InlineData("first_frame.png")][InlineData("last_frame.png")][InlineData("marks.json")]
    public void FiveAttachmentRolesProtectBeforeFilesystem(string name)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("candidate");
        f.Db.Ordinary.Read(row.RecordingId);Insert(f,row.PublicationReceipt!.Anchor,name);
        var page=f.Page();Assert.Equal("shared_publication_identity",Assert.Single(page.Items).ReasonCode);
        RetentionRuntimeOutputTests.NoProbes(f);Assert.DoesNotContain("SECRET",JsonSerializer.Serialize(page));
    }
    [Theory][InlineData("volume")][InlineData("low")][InlineData("high")]
    public void FullTupleNegativeControls(string part)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("candidate");var a=row.PublicationReceipt!.Anchor;
        var id=a.Identity;Insert(f,a with {Identity=part=="volume"?id with {VolumeSerialNumber=id.VolumeSerialNumber^1}:part=="low"?id with {IdLow=id.IdLow^1}:id with {IdHigh=id.IdHigh^1}});
        Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);Assert.Single(f.Files.Observations);
    }
    [Theory][InlineData("candidate")][InlineData("CANDIDATE")][InlineData("SECRET_FOREIGN")]
    public void RoleHasNoOwnerExclusionAndUsesOneIndexedBatch(string owner)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("candidate");Insert(f,row.PublicationReceipt!.Anchor,owner:owner);
        using var c=f.Db.Store.OpenReadOnlyConnection();using var t=c.BeginTransaction();var calls=0;
        var anchors=Enumerable.Range(0,100).ToDictionary(i=>i==0?"candidate":"id"+i,i=>(PublicationIdentityAnchor?)row.PublicationReceipt.Anchor);
        var result=PublicationIdentityReferenceStorage.Read(c,t,anchors,(connection,transaction,sql,args)=>{
            calls++;Assert.Same(c,connection);Assert.Same(t,transaction);Assert.Equal(300,args.Count);
            Assert.DoesNotContain("JOIN",sql);Assert.DoesNotContain("output_path",sql);
            using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+sql;
            foreach(var p in args)cmd.Parameters.AddWithValue(p.Key,p.Value);
            var plan=new List<string>();using var rd=cmd.ExecuteReader();while(rd.Read())plan.Add(rd.GetString(3));
            foreach(var index in SqliteSchemaV42.Indexes.Select(i=>i.Name).Append(SqliteSchemaV45.IndexName))Assert.Contains(plan,p=>p.Contains("SEARCH a USING COVERING INDEX "+index)&&p.Contains("volume_id=? AND file_id=?"));
            Assert.DoesNotContain(plan,p=>p.Contains("SCAN a"));
            output.WriteLine(string.Join("\n",plan));
        },null);
        Assert.Equal(1,calls);Assert.All(result.Values,v=>Assert.Equal(PublicationIdentityReferenceState.Referenced,v));
    }
    [Theory][InlineData("source")][InlineData("name")][InlineData("key")]
    public void BadForeignBusinessStillProtectsButMalformedUnmatchedKeyIsLimited(string fault)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("candidate");Insert(f,row.PublicationReceipt!.Anchor);
        f.Db.Corrupt(fault=="key"?"UPDATE ordinary_bundle_identity_items SET file_id=x'01';":fault=="name"?"UPDATE ordinary_bundle_identity_items SET name='SECRET_BAD';":"UPDATE ordinary_bundle_identity_items SET source='SECRET_BAD',output_size_bytes=-1;");
        Assert.Equal(fault=="key"?"age_rule_match":"shared_publication_identity",Assert.Single(f.Page().Items).ReasonCode);
        if(fault!="key")RetentionRuntimeOutputTests.NoProbes(f);
    }
    [Fact]
    public async Task NewReferenceAfterEarlierPreviewStopsGuardAndComposedContinuation()
    {
        using(var f=new RetentionPreviewQueryTests.Fixture()){
            var row=f.Seed("candidate");Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);
            Insert(f,row.PublicationReceipt!.Anchor);using var g=new RetentionReferenceGuard(f.Db.Store,f.Db.Ordinary,Sid,Session,new());var calls=0;
            var reason=await RetentionGuardFixture.Finish(g.Execute(row.RecordingId,row.PublicationReceipt.Anchor,f.Before,_=>{calls++;return RetentionGuardContinuationResult.Completed;}));
            Assert.Equal(RetentionGuardReason.IdentityProtected,reason);Assert.Equal(0,calls);
        }
        using var composed=new RetentionComposedFixture();
        Assert.Equal(PublicationIdentityReferenceState.None,Assert.Single(composed.Db.Query.GetOrdinaryEvidencePage(Sid,Session,20,composed.Cutoff,null).Items).PublicationIdentityReference);
        InsertDb(composed.Db,composed.Anchor);
        using var scope=composed.Service();var continuations=0;
        var outcome=await RetentionComposedFixture.Finish(composed.Execute(scope,_=>{continuations++;return RetentionComposedContinuationResult.Completed;}));
        Assert.Equal(RetentionGuardReason.IdentityProtected,outcome.Database);Assert.Equal(0,continuations);
    }
    [Fact]
    public async Task ApprovedSingleDeletionRechecksNewAttachmentBeforeAnyNativeDisposition()
    {
        using var f=new LocalRetentionDeletionFixture();using var a=f.Service();
        var pending=a.Request(f.Row.RecordingId);var proof=await f.Approve(a,pending);
        InsertDb(f.Db,f.Anchor);
        var result=await LocalRetentionDeletionFixture.Finish(a.RequestDeletion(proof,f.Row.RecordingId),pending);
        Assert.Equal(LocalDeletionProgress.NotAttempted,result.Progress);Assert.Equal(0,f.Native.Calls);
        Assert.Equal(f.Bytes,File.ReadAllBytes(f.Media));
    }
    [Fact]
    public async Task FormalBundlePersistenceSuppliesReferenceWithoutCorruption()
    {
        using var f=new BundlePublicationHistoryTests.Fixture();var op=f.Seed();var update=await f.Produced(op);f.Accept(update);
        var a=update.Result.IdentityEvidence!.Anchors[0];var row=f.Row("candidate") with {BundleRegistration=new(null),PublicationReceipt=PublicationReceipt.IssueForTests(f.Row("candidate").OutputPath,100,a.Identity)};
        f.Writer.Register(row);f.Writer.Read(row.RecordingId);
        var page=f.Query.GetOrdinaryEvidencePage(Sid,Session,20,At.AddHours(1),null);
        Assert.Equal(PublicationIdentityReferenceState.Referenced,page.Items.Single(i=>i.Entry.RecordingId=="candidate").PublicationIdentityReference);
    }
    [Fact]
    public void CursorHighWaterDoesNotHideLaterAttachment()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var b=f.Seed("b");var first=f.Page(1);Assert.NotNull(first.NextCursor);
        Insert(f,b.PublicationReceipt!.Anchor);Assert.Equal("shared_publication_identity",Assert.Single(f.Page(1,first.NextCursor).Items).ReasonCode);
    }
    [Fact]
    public void Genuine44UpgradeLeavesAllOldTypedRowsObjectsReceiptsUntouched()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("candidate");f.Db.Ordinary.Read("candidate");
        f.Db.RawExecute("DROP TABLE ordinary_deletion_host_rejections; DELETE FROM schema_migrations WHERE version=51; DROP TABLE ordinary_deletion_batch_policy_children; DROP TABLE ordinary_deletion_batch_policies; DELETE FROM schema_migrations WHERE version=50; DROP TABLE ordinary_deletion_operation_policies; DROP TABLE ordinary_deletion_request_policies; DELETE FROM schema_migrations WHERE version=49; DROP TABLE ordinary_deletion_batch_items; DROP TABLE ordinary_deletion_batches; DELETE FROM schema_migrations WHERE version=48; DROP INDEX idx_v47_ordinary_recording_bundles_namespace; DROP INDEX idx_v47_ordinary_bundle_contents_namespace; DROP INDEX idx_v47_ordinary_bundle_identity_namespace; DROP INDEX idx_v47_ordinary_media_hash_namespace; DROP INDEX idx_v47_ordinary_bundle_attachment_hash_namespace; DELETE FROM schema_migrations WHERE version=47; DROP INDEX idx_v46_ordinary_recording_bundles_path; DROP INDEX idx_v46_ordinary_bundle_contents_path; DROP INDEX idx_v46_ordinary_bundle_identity_path; DROP INDEX idx_v46_ordinary_media_hash_path; DROP INDEX idx_v46_ordinary_bundle_attachment_hash_path; DROP INDEX idx_v46_ordinary_bundle_identity_items_path; DROP INDEX idx_v46_ordinary_bundle_attachment_hash_items_path; DROP INDEX idx_v46_ordinary_media_hash_identity; DROP INDEX idx_v46_ordinary_bundle_attachment_hash_items_identity; DELETE FROM schema_migrations WHERE version=46; DROP INDEX idx_bundle_attachment_publication_identity; DELETE FROM schema_migrations WHERE version=45;");
        var before=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true,44);
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());RetentionRuntimeOutputTests.NoProbes(f);
        f.Db.Store.Initialize();Assert.Equal(before,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true,44));
        Assert.Equal(51,f.Db.Scalar("SELECT max(version) FROM schema_migrations;"));
        var once=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true);f.Db.Store.Initialize();
        Assert.Equal(once,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true));
        f.Db.RawExecute($"INSERT INTO schema_migrations VALUES({SqliteOperationalStore.CurrentSchemaVersion+1},'future','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',0);");
        Assert.ThrowsAny<Exception>(()=>f.Db.Store.Initialize());
    }
}
