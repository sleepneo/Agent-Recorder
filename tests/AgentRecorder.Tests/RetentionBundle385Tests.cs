using System.Buffers.Binary;
using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class RetentionBundle385Tests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Sources => new[] { "ordinary_media_hash:identity", "ordinary_bundle_attachment_hash_items:identity",
        "ordinary_recording_bundles:path", "ordinary_bundle_contents:path", "ordinary_bundle_identity:path", "ordinary_media_hash:path",
        "ordinary_bundle_attachment_hash:path", "ordinary_bundle_identity_items:path", "ordinary_bundle_attachment_hash_items:path" }.Select(s => new object[] { s });

    // Deliberately orphaned/corrupted saved facts, not playable duplicate publications.
    internal static void Insert(UnifiedHistoryTestDatabase db, string source, string path, PublicationIdentityAnchor anchor,
        string owner = "SECRET_FOREIGN", string difference = "none")
    {
        var table = source.Split(':')[0]; var identity = source.EndsWith(":identity");
        var v = new byte[8]; var id = new byte[16]; var a = anchor.Identity;
        BinaryPrimitives.WriteUInt64LittleEndian(v, a.VolumeSerialNumber ^ (difference == "volume" ? 1UL : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(id, a.IdLow ^ (difference == "low" ? 1UL : 0));
        BinaryPrimitives.WriteUInt64LittleEndian(id.AsSpan(8), a.IdHigh ^ (difference == "high" ? 1UL : 0));
        var p = identity || difference == "path" ? "C:\\SECRET\\different.mp4" : path.ToUpperInvariant();
        string sql;
        if (table.EndsWith("_items"))
            sql = table == "ordinary_bundle_identity_items"
                ? $"INSERT INTO {table} VALUES($owner,0,'metadata.json',$path,999,1,'bundle_copy_handle/v1',$v,$id);"
                : $"INSERT INTO {table} VALUES($owner,0,'metadata.json',$path,999,'sha256',$hash,'bundle_publication',$v,$id);";
        else if (table == "ordinary_recording_bundles")
            sql = $"INSERT INTO {table}(recording_id,media_output_path,status_code) VALUES($owner,$path,'SECRET_BAD');";
        else if (table == "ordinary_media_hash")
            sql = $"INSERT INTO {table}(recording_id,media_output_path,volume_id,file_id,hash_value) VALUES($owner,$path,$v,$id,$hash);";
        else
            sql = $"INSERT INTO {table}(recording_id,media_output_path,{(table == "ordinary_bundle_contents" ? "contents_count" : "item_count")}) VALUES($owner,$path,0);";
        // Path-only media-hash rows must not also match the new identity source.
        if (!identity) v[0] ^= 0x80;
        db.Corrupt(sql, ("$owner", owner), ("$path", p), ("$v", v), ("$id", id), ("$hash", new string('a',64)));
    }

    [Theory, MemberData(nameof(Sources))]
    public void IsolatedSourceProtectsBeforeAnyFileProbe(string source)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate");
        Insert(f.Db, source, row.OutputPath, row.PublicationReceipt!.Anchor);
        var page = f.Page(); Assert.Equal(source.EndsWith(":identity") ? "shared_publication_identity" : "shared_output_path", Assert.Single(page.Items).ReasonCode);
        RetentionRuntimeOutputTests.NoProbes(f); Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(page));
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task EverySourceInsertedAfterApprovalBlocksNativeDeletion(string source)
    {
        using var f = new LocalRetentionDeletionFixture(); using var service = f.Service();
        var pending = service.Request(f.Row.RecordingId); var proof = await f.Approve(service,pending);
        Insert(f.Db,source,f.Media,f.Anchor);
        var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(proof,f.Row.RecordingId),pending);
        Assert.Equal(LocalDeletionProgress.NotAttempted,result.Progress); Assert.Equal(0,f.Native.Calls);
        Assert.Equal(f.Bytes,File.ReadAllBytes(f.Media));
    }

    [Theory, MemberData(nameof(Sources))]
    public async Task EverySourceRecheckedByGuard(string source)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate");
        Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);
        Insert(f.Db,source,row.OutputPath,row.PublicationReceipt!.Anchor);
        using var guard = new RetentionReferenceGuard(f.Db.Store,f.Db.Ordinary,Sid,Session,new()); var calls = 0;
        var reason = await RetentionGuardFixture.Finish(guard.Execute(row.RecordingId,row.PublicationReceipt.Anchor,f.Before,_ => { calls++; return RetentionGuardContinuationResult.Completed; }));
        Assert.Equal(source.EndsWith(":identity") ? RetentionGuardReason.IdentityProtected : RetentionGuardReason.SharedPathProtected,reason); Assert.Equal(0,calls);
    }

    [Theory, MemberData(nameof(Sources))]
    public void DifferentKeyDoesNotInventReference(string source)
    {
        foreach(var difference in source.EndsWith(":identity") ? new[] { "volume","low","high" } : new[] { "path" })
        {
            using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate");
            Insert(f.Db,source,row.OutputPath,row.PublicationReceipt!.Anchor,difference:difference);
            if (source.EndsWith(":identity"))
            {
                // Equal saved digest text is not physical identity. Deliberately
                // corrupt only the own hash text for this raw relation check;
                // restore it before exercising the validated preview pipeline.
                f.Db.Corrupt("UPDATE ordinary_media_hash SET hash_value=$hash WHERE recording_id='candidate';",("$hash",new string('a',64)));
                using (var c=f.Db.Store.OpenReadOnlyConnection())
                using (var t=c.BeginTransaction())
                {
                    using var proof=c.CreateCommand();proof.Transaction=t;
                    proof.CommandText="SELECT hash_value FROM ordinary_media_hash WHERE recording_id='candidate';";
                    Assert.Equal(new string('a',64),proof.ExecuteScalar());
                    proof.CommandText="SELECT hash_value FROM "+source.Split(':')[0]+" WHERE recording_id='SECRET_FOREIGN';";
                    Assert.Equal(new string('a',64),proof.ExecuteScalar());
                    Assert.Equal(PublicationIdentityReferenceState.None,PublicationIdentityReferenceStorage.Read(c,t,
                        new Dictionary<string,PublicationIdentityAnchor?> { [row.RecordingId]=row.PublicationReceipt.Anchor },null,null)[row.RecordingId]);
                }
                f.Db.Corrupt("UPDATE ordinary_media_hash SET hash_value=NULL WHERE recording_id='candidate';");
            }
            Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);
        }
    }

    [Theory, MemberData(nameof(Sources))]
    public void ExactSelfOnlyForMainMediaAndCaseDistinctOwnerProtects(string source)
    {
        foreach(var owner in new[] { "candidate","CANDIDATE" })
        {
            using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate");
            // Replace only this association. No parent/receipt trust is inferred.
            f.Db.Corrupt($"DELETE FROM {source.Split(':')[0]} WHERE recording_id=$id;",("$id",owner));
            Insert(f.Db,source,row.OutputPath,row.PublicationReceipt!.Anchor,owner);
            var selfMain = owner == "candidate" && !source.Contains("_items:");
            // Invalid own bundle facts are not lawful candidate evidence; exercise raw relation reads instead.
            using var c = f.Db.Store.OpenReadOnlyConnection(); using var t = c.BeginTransaction();
            if(source.EndsWith(":identity"))
                Assert.Equal(selfMain ? PublicationIdentityReferenceState.None : PublicationIdentityReferenceState.Referenced,
                    PublicationIdentityReferenceStorage.Read(c,t,new Dictionary<string,PublicationIdentityAnchor?> { [row.RecordingId] = row.PublicationReceipt.Anchor },null,null)[row.RecordingId]);
            else
            {
                var method = typeof(UnifiedHistoryQueryService).GetMethod("ReadSharedOutputReferences",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
                var flags = (Dictionary<string,bool>)method.Invoke(f.Db.Query,new object[] {c,t,new[] {row}})!;
                Assert.Equal(!selfMain,flags[row.RecordingId]);
            }
        }
    }

    [Fact]
    public void HundredCandidatesUseSameTransactionAndEveryCoveringIndex()
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        var rows = Enumerable.Range(0,100).Select(i=>f.Seed("r"+i)).ToArray();
        foreach(var source in Sources.Select(s=>(string)s[0])) Insert(f.Db,source,rows[0].OutputPath,rows[0].PublicationReceipt!.Anchor,source.Replace(':','_'));
        Microsoft.Data.Sqlite.SqliteConnection? connection = null; Microsoft.Data.Sqlite.SqliteTransaction? transaction = null;
        var calls = 0;
        void Inspect(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t, string sql, IReadOnlyList<KeyValuePair<string,object>> args)
        {
            if(connection is null) { connection=c; transaction=t; } else { Assert.Same(connection,c); Assert.Same(transaction,t); }
            calls++; Assert.Equal(calls==1 ? 300 : 200,args.Count);
            using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="EXPLAIN QUERY PLAN "+sql;
            foreach(var p in args)cmd.Parameters.AddWithValue(p.Key,p.Value);
            var plan=new List<string>();using var rd=cmd.ExecuteReader();while(rd.Read())plan.Add(rd.GetString(3));
            foreach(var index in SqliteSchemaV46.Indexes.Where(i=>i.Name.EndsWith(calls==1 ? "_identity" : "_path")))
                Assert.Contains(plan,p=>p.Contains("SEARCH ")&&p.Contains("USING COVERING INDEX "+index.Name));
            foreach(var index in calls==1 ? SqliteSchemaV42.Indexes.Select(i=>i.Name).Append(SqliteSchemaV45.IndexName) : SqliteSchemaV28.Paths.Select(i=>i.Index))
                Assert.Contains(plan,p=>p.Contains("SEARCH ")&&p.Contains(index));
            Assert.DoesNotContain(plan,p=>p.Contains("SCAN a")||p.Contains("SCAN referenced"));output.WriteLine(string.Join("\n",plan));
        }
        f.Db.Query.RetentionIdentityCommandForTests=Inspect; f.Db.Query.RetentionPathCommandForTests=Inspect;
        Assert.Equal(100,f.Db.Query.GetOrdinaryEvidencePage(Sid,Session,100,f.Before,null).Items.Count);Assert.Equal(2,calls);
        RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Fact]
    public void V45UpgradePreservesAllOldTypedDataAndObjects()
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("candidate");
        f.Db.RawExecute("DROP TABLE ordinary_deletion_batch_items; DROP TABLE ordinary_deletion_batches; DELETE FROM schema_migrations WHERE version=48; DROP INDEX idx_v47_ordinary_recording_bundles_namespace; DROP INDEX idx_v47_ordinary_bundle_contents_namespace; DROP INDEX idx_v47_ordinary_bundle_identity_namespace; DROP INDEX idx_v47_ordinary_media_hash_namespace; DROP INDEX idx_v47_ordinary_bundle_attachment_hash_namespace; DELETE FROM schema_migrations WHERE version=47; "+string.Join(";",SqliteSchemaV46.Indexes.Select(i=>"DROP INDEX "+i.Name))+"; DELETE FROM schema_migrations WHERE version=46;");
        var before=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true,45);
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());RetentionRuntimeOutputTests.NoProbes(f);
        f.Db.Store.Initialize();Assert.Equal(before,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true,45));
        var once=SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true);f.Db.Store.Initialize();
        Assert.Equal(once,SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath,true));
        Assert.Equal(48,f.Db.Scalar("SELECT max(version) FROM schema_migrations;"));
    }

    [Theory, MemberData(nameof(Sources))]
    public void CursorHighWaterDoesNotHideNewOrphanReference(string source)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();f.Seed("a");var b=f.Seed("b");var first=f.Page(1);Assert.NotNull(first.NextCursor);
        Insert(f.Db,source,b.OutputPath,b.PublicationReceipt!.Anchor);
        Assert.Equal(source.EndsWith(":identity") ? "shared_publication_identity" : "shared_output_path",Assert.Single(f.Page(1,first.NextCursor).Items).ReasonCode);
    }

    [Theory, MemberData(nameof(Sources))]
    public void BinaryDamagedOwnerCannotBeMistakenForSelf(string source)
    {
        using var f=new RetentionPreviewQueryTests.Fixture();var row=f.Seed("candidate");
        Insert(f.Db,source,row.OutputPath,row.PublicationReceipt!.Anchor);
        f.Db.Corrupt($"UPDATE {source.Split(':')[0]} SET recording_id=CAST('candidate' AS BLOB) WHERE recording_id='SECRET_FOREIGN';");
        Assert.Equal(source.EndsWith(":identity") ? "shared_publication_identity" : "shared_output_path",Assert.Single(f.Page().Items).ReasonCode);
        RetentionRuntimeOutputTests.NoProbes(f);
    }
}
