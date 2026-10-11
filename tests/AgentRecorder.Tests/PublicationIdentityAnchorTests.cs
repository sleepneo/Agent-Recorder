using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PublicationIdentityAnchorTests
{
    internal sealed class Native : IWindowsFileMetadataApi
    {
        private readonly IWindowsFileMetadataApi _real = WindowsFileMetadataApi.Instance;
        internal readonly List<SafeFileHandle> Handles = new();
        internal int Basics, Finals;
        internal string? Fault;
        internal Action<int>? AtBasic;
        public bool Supported => Fault != "unsupported";
        public uint DriveType(string root) => _real.DriveType(root);
        public SafeFileHandle Open(string p,uint a,uint s,uint c,uint f,out int error) => throw new InvalidOperationException("Must retain original create handle, never reopen.");
        public bool Basic(SafeFileHandle h,out FileBasicInformation i,out int error)
        {
            Basics++; Handles.Add(h); AtBasic?.Invoke(Basics);
            var ok=_real.Basic(h,out i,out error);
            if(Fault=="throw")throw new IOException("PRIVATE native exception");
            if(Fault=="basic" || Fault=="after_basic"&&Basics==2){error=50;return false;}
            if(Fault=="attributes"&&Basics==2)i.Attributes^=(uint)FileAttributes.Hidden;
            if(Fault=="write_time"&&Basics==2)i.LastWriteTime++;
            return ok;
        }
        public bool Standard(SafeFileHandle h,out FileStandardInformation i,out int error)
        {
            var ok=_real.Standard(h,out i,out error);
            if(Fault=="standard"){error=50;return false;}
            if(Fault=="hardlink")i.NumberOfLinks=2;
            if(Fault=="zero_links")i.NumberOfLinks=0;
            if(Fault=="deleted")i.DeletePending=1;
            if(Fault=="size"&&Basics==2)i.EndOfFile++;
            return ok;
        }
        public bool Identity(SafeFileHandle h,out FileIdentityInformation i,out int error)
        {
            var ok=_real.Identity(h,out i,out error);
            if(Fault=="identity"){error=50;return false;}
            if(Fault=="zero_id")i.IdLow=i.IdHigh=0;
            if(Fault=="high"&&Basics==2)i.IdHigh^=1;
            if(Fault=="low"&&Basics==2)i.IdLow^=1;
            if(Fault=="volume"&&Basics==2)i.VolumeSerialNumber^=1;
            return ok;
        }
        public uint FinalPath(SafeFileHandle h,StringBuilder b,int capacity,out int error)
        {
            Finals++;var count=_real.FinalPath(h,b,capacity,out error);
            if(Fault=="buffer")return (uint)capacity;
            if(Fault=="path"){b.Clear().Append(@"\\?\C:\PRIVATE\other.mp4");return (uint)b.Length;}
            return count;
        }
    }
    internal static OrdinaryRecordingHistorySnapshot Completed(UnifiedHistoryTestDatabase db,string path, PublicationReceipt? receipt) =>
        db.Recording("published",completed:true) with{OutputPath=path,PublicationReceipt=receipt,BundleRegistration=new(null)};
    internal static RetentionPreviewPage Preview(UnifiedHistoryTestDatabase db,bool headless=false) =>
        new RetentionPreviewQueryService(new UnifiedHistoryQueryService(db.Store,db.Ordinary,headless),db.Root).GetPage(Sid,Session,20,At.AddHours(1));
    [Fact]
    public async Task RealPublisherKeepsOriginalCopyHandleAcrossMoveAndSignsOnlyThatCopy()
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var source=f.Create("staging.mp4");var final=Path.Combine(f.Videos,"final.mp4");
        var sourceId=WindowsReadOnlyFileMetadataProbe.Instance.Observe(source).Before!.Identity;
        var api=new Native();var p=new StagingToFinalPublisher(new PhysicalFileSystemOperations(),identityApi:api);
        var result=await p.PublishAsync(source,final);Assert.True(result.Success);Assert.NotNull(result.Receipt);
        Assert.Equal(2,api.Basics);Assert.Equal(2,api.Finals);Assert.Same(api.Handles[0],api.Handles[1]);Assert.All(api.Handles,h=>Assert.True(h.IsClosed));
        var current=WindowsReadOnlyFileMetadataProbe.Instance.Observe(final);Assert.Equal(result.Receipt!.Anchor.Identity,current.Before!.Identity);
        Assert.NotEqual(sourceId,current.Before.Identity);Assert.Equal(final,result.Receipt.Anchor.Path);Assert.Equal(12,result.Receipt.Anchor.Length);
        using var exclusive=new FileStream(final,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.Equal(12,exclusive.Length);
    }
    [Fact]
    public async Task RealWriteHandleBlocksAnotherWriterButAllowsOwnRenameAndOverwritePolicy()
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var source=f.Create("source.mp4");var final=f.Create("final.mp4",7);var old=WindowsReadOnlyFileMetadataProbe.Instance.Observe(final).Before!.Identity;
        var api=new Native();var conflict=false;
        api.AtBasic=n=>{if(n!=1)return;var tmp=Directory.GetFiles(f.Videos,"*.publish-tmp-*.mp4").Single();var error=Assert.Throws<IOException>(()=>{using var writer=new FileStream(tmp,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);});conflict=(error.HResult&0xffff) is 32 or 33;};
        var result=await new StagingToFinalPublisher(new PhysicalFileSystemOperations(),identityApi:api).PublishAsync(source,final);
        Assert.True(conflict);Assert.True(result.Success);Assert.NotNull(result.Receipt);Assert.NotEqual(old,result.Receipt!.Anchor.Identity);Assert.All(api.Handles,h=>Assert.True(h.IsClosed));
    }
    [Theory]
    [InlineData("unsupported")][InlineData("basic")][InlineData("after_basic")][InlineData("standard")][InlineData("identity")][InlineData("throw")]
    [InlineData("hardlink")][InlineData("zero_links")][InlineData("deleted")][InlineData("zero_id")][InlineData("buffer")][InlineData("path")]
    [InlineData("attributes")][InlineData("write_time")][InlineData("size")][InlineData("volume")][InlineData("low")][InlineData("high")]
    public async Task OptionalUnsupportedInvalidOrChangedObservationCannotSignAndDoesNotUndoCommit(string fault)
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var source=f.Create();var final=Path.Combine(f.Videos,"final.mp4");var api=new Native{Fault=fault};
        var result=await new StagingToFinalPublisher(new PhysicalFileSystemOperations(),identityApi:api).PublishAsync(source,final);
        Assert.True(result.Success);Assert.Null(result.Receipt);Assert.Equal(12,result.FinalSizeBytes);Assert.True(File.Exists(final));Assert.All(api.Handles,h=>Assert.True(h.IsClosed));
    }
    [Theory]
    [InlineData("closed")][InlineData("cancelled")][InlineData("rename_before")][InlineData("replace_after")]
    public async Task RealCancelClosedGateRenameAndReplacementNeverSignWrongFile(string fault)
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var source=f.Create();var final=Path.Combine(f.Videos,"final.mp4");var api=new Native();var gate=new FileCommitGate();using var cancel=new CancellationTokenSource();
        if(fault=="closed")gate.Close();if(fault=="cancelled")cancel.Cancel();
        api.AtBasic=n=>{
            if(fault=="rename_before"&&n==1)File.Move(Directory.GetFiles(f.Videos,"*.publish-tmp-*.mp4").Single(),Path.Combine(f.Videos,"held.mp4"));
            if(fault=="replace_after"&&n==2){File.Move(final,Path.Combine(f.Videos,"held.mp4"));f.Create("final.mp4");}
        };
        var result=await new StagingToFinalPublisher(new PhysicalFileSystemOperations(),identityApi:api).PublishAsync(source,final,cancel.Token,gate);
        Assert.Equal(fault is "replace_after" or "rename_before",result.Success);Assert.Null(result.Receipt);Assert.All(api.Handles,h=>Assert.True(h.IsClosed));
        Assert.Empty(Directory.GetFiles(f.Videos,"*.publish-tmp-*.mp4"));Assert.Equal(fault is "replace_after" or "rename_before",File.Exists(final));
        if(fault=="rename_before")Assert.Equal(12,new FileInfo(final).Length);
        gate.Close();Assert.False(gate.TryCommit(()=>Assert.Fail("late commit")));
    }
    [Fact]
    public async Task CreateNewCollisionDoesNotDeleteSomeoneElsesTemporaryFile()
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var source=f.Create();var final=Path.Combine(f.Videos,"final.mp4");var collision=f.Create("final.mp4.publish-tmp-collision.mp4",7);
        var result=await new StagingToFinalPublisher(new PhysicalFileSystemOperations(),()=>"collision").PublishAsync(source,final);
        Assert.False(result.Success);Assert.Null(result.Receipt);Assert.Equal(7,new FileInfo(collision).Length);Assert.False(File.Exists(final));
    }
    [Theory]
    [InlineData("copy")][InlineData("flush")][InlineData("cancel_after_copy")][InlineData("close_after_copy")]
    public async Task OriginalWriteHandleClosesOnInjectedCopyFlushAndRealCancelOrCloseBeforeCommit(string fault)
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var source=f.Create();var final=f.Create("final.mp4",7);var old=WindowsReadOnlyFileMetadataProbe.Instance.Observe(final).Before!.Identity;SafeFileHandle? original=null;using var cancellation=new CancellationTokenSource();var gate=new FileCommitGate();
        var publisher=new StagingToFinalPublisher();publisher.OriginalCopyCreatedForTests=stream=>{original=stream.SafeFileHandle;if(fault=="copy")throw new IOException("injected copy setup failure");};
        publisher.BeforeOriginalFlushForTests=_=>{if(fault=="flush")throw new IOException("injected flush failure");if(fault=="cancel_after_copy")cancellation.Cancel();if(fault=="close_after_copy")gate.Close();};
        var result=await publisher.PublishAsync(source,final,cancellation.Token,gate);Assert.False(result.Success);Assert.Null(result.Receipt);Assert.NotNull(original);Assert.True(original!.IsClosed);Assert.Empty(Directory.GetFiles(f.Videos,"*.publish-tmp-*.mp4"));Assert.Equal(old,WindowsReadOnlyFileMetadataProbe.Instance.Observe(final).Before!.Identity);Assert.Equal(7,new FileInfo(final).Length);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RealPublishedReceiptPersistsAndSameSizeCopiedTimestampReplacementIsMismatch(bool headless)
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var source=f.Create("source.mp4");var final=Path.Combine(f.Videos,"final.mp4");var result=await StagingToFinalPublisher.Instance.PublishAsync(source,final);
        Assert.NotNull(result.Receipt);var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            var completed=Completed(db,final,result.Receipt);var accepted=completed with{Status="preparing",CompletedAtUtc=null,StartedAtUtc=null,OutputSizeBytes=null,OutputDurationSeconds=null,PublicationReceipt=null,BundleRegistration=null};db.Ordinary.Register(accepted);Assert.True(db.Ordinary.TryPublish(completed));Assert.Equal("completed",db.Ordinary.Read("published")!.Status);
            Assert.Equal("age_rule_match",Assert.Single(Preview(db,headless).Items).ReasonCode);var before=db.Snapshot();var write=File.GetLastWriteTimeUtc(final);
            File.Move(final,Path.Combine(f.Videos,"held.mp4"));f.Create("final.mp4");File.SetLastWriteTimeUtc(final,write);
            var page=Preview(db,headless);Assert.Equal("publication_identity_mismatch",Assert.Single(page.Items).ReasonCode);Assert.Null(page.Items[0].ObservedFileBytes);Assert.Equal(1,page.UnknownCandidateFileCount);Assert.Equal(0,page.ObservedCandidateBytes);Assert.Equal(before,db.Snapshot());
        }finally{db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [Fact]
    public void UnanchoredOldRecordIsMissingBeforeAllFileProbesAndExistingReferencePrioritySurvives()
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            db.Ordinary.Register(Completed(db,Path.Combine(f.Videos,"absent.mp4"),null));var files=new RetentionPreviewQueryTests.MetadataFake{Attribute=_=>throw new Exception("probe must not run"),Observation=_=>throw new Exception("probe must not run")};
            var service=new RetentionPreviewQueryService(db.Query.GetOrdinaryEvidencePage,db.Root,files,files,()=>At.AddDays(5));var page=service.GetPage(Sid,Session,20,At.AddHours(1));Assert.Equal("publication_identity_missing",Assert.Single(page.Items).ReasonCode);Assert.Empty(files.Attributes);Assert.Empty(files.Observations);Assert.Null(page.Items[0].ObservedFileBytes);Assert.Equal(1,page.UnknownCandidateFileCount);
            db.Ordinary.Register(db.Recording("child",At.AddDays(-1)) with{ParentRecordingId="published"});Assert.Equal("nested_parent_referenced",Assert.Single(service.GetPage(Sid,Session,1,At.AddHours(1)).Items).ReasonCode);Assert.Empty(files.Attributes);
        }finally{db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [Theory]
    [InlineData("volume")][InlineData("low")][InlineData("high")]
    public void AnchorComparisonPreservesAllUnsignedBitsAndUnknownBytes(string part)
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();f.Seed();var stable=RetentionHandleMetadataTests.Stable();var id=stable.Identity!;id=part switch{"volume"=>id with{VolumeSerialNumber=id.VolumeSerialNumber^1},"low"=>id with{IdLow=id.IdLow^1},_=>id with{IdHigh=id.IdHigh^1}};
        f.Files.Observation=p=>new(FileObservationOutcome.Sampled,stable with{Identity=id},stable with{Identity=id},p,p);var page=f.Page();Assert.Equal("publication_identity_mismatch",Assert.Single(page.Items).ReasonCode);Assert.Null(page.Items[0].ObservedFileBytes);Assert.Equal(1,page.UnknownCandidateFileCount);
    }
    [Theory]
    [InlineData("path")][InlineData("length")][InlineData("failed")][InlineData("active")][InlineData("invalid_id")]
    public void ReceiptBindingCannotBeAcceptedFromWrongPathLengthOrNonCompletedResult(string fault)
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            var row=PublicationAnchorTestFixture.Anchored(Completed(db,Path.Combine(f.Videos,"final.mp4"),null));row=fault switch{"path"=>row with{OutputPath=Path.Combine(f.Videos,"other.mp4")},"length"=>row with{OutputSizeBytes=13},"failed"=>row with{Status="failed"},"active"=>row with{Status="recording"},_=>row with{PublicationReceipt=PublicationReceipt.IssueForTests(row.OutputPath,12,new(0,0,0))}};
            Assert.Throws<ArgumentException>(()=>db.Ordinary.Register(row));Assert.Equal(0,db.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));
        }finally{db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [Fact]
    public void TerminalWinnerReceiptIsAtomicIdempotentAndCannotBeChangedClearedOrAddedLater()
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            var row=PublicationAnchorTestFixture.Anchored(Completed(db,Path.Combine(f.Videos,"final.mp4"),null));db.Ordinary.Register(row);var before=db.Snapshot();Assert.True(db.Ordinary.TryPublish(row));db.Ordinary.Read(row.RecordingId);Assert.Equal(before,db.Snapshot());
            foreach(var conflict in new[]{row with{PublicationReceipt=null},row with{Status="failed",PublicationReceipt=null},row with{PublicationReceipt=PublicationReceipt.IssueForTests(row.OutputPath,12,new(1,2,3))}}){Assert.True(db.Ordinary.TryPublish(conflict));db.Ordinary.Read(row.RecordingId);Assert.Equal(before,db.Snapshot());}
            var old=row with{RecordingId="old",PublicationReceipt=null};db.Ordinary.Register(old);Assert.True(db.Ordinary.TryPublish(old with{PublicationReceipt=row.PublicationReceipt}));db.Ordinary.Read("old");Assert.Equal(1,db.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));
            db.Ordinary.Register(db.Recording("unfinished"));db.Ordinary.RecoverIncomplete(At.AddHours(2));Assert.Equal("interrupted_unknown",db.Ordinary.Read("unfinished")!.Status);Assert.Equal(1,db.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));
            var after=db.Snapshot();db.Store.Initialize();Assert.Equal(after,db.Snapshot());
        }finally{db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [Fact]
    public void FailedAnchorWriteRollsBackTerminalAndReadBarrierFailsUntilExactReceiptRetry()
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var db=new UnifiedHistoryTestDatabase(f.Root);try
        {
            var row=PublicationAnchorTestFixture.Anchored(Completed(db,Path.Combine(f.Videos,"final.mp4"),null));var accepted=row with{Status="preparing",StartedAtUtc=null,CompletedAtUtc=null,OutputSizeBytes=null,OutputDurationSeconds=null,PublicationReceipt=null,BundleRegistration=null};db.Ordinary.Register(accepted);
            db.Execute("CREATE TRIGGER reject_anchor BEFORE INSERT ON ordinary_publication_anchors BEGIN SELECT RAISE(ABORT,'PRIVATE'); END;");Assert.True(db.Ordinary.TryPublish(row));Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(()=>db.Ordinary.Read(row.RecordingId));Assert.Equal(0,db.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));Assert.Equal(1,db.Scalar("SELECT COUNT(*) FROM ordinary_recording_history WHERE status_code='preparing';"));
            db.Execute("DROP TRIGGER reject_anchor;");Assert.True(db.Ordinary.TryPublish(row));Assert.Equal("completed",db.Ordinary.Read(row.RecordingId)!.Status);Assert.Equal(1,db.Scalar("SELECT COUNT(*) FROM ordinary_publication_anchors;"));
        }finally{db.Ordinary.Dispose();SqliteConnection.ClearAllPools();}
    }
    [Theory]
    [InlineData("DROP TABLE ordinary_publication_anchors;")]
    [InlineData("UPDATE ordinary_publication_anchors SET volume_id=zeroblob(8);")]
    [InlineData("UPDATE ordinary_publication_anchors SET file_id=zeroblob(16);")]
    [InlineData("UPDATE ordinary_publication_anchors SET file_id=x'0102';")]
    [InlineData("UPDATE ordinary_publication_anchors SET output_size_bytes=12.5;")]
    [InlineData("UPDATE ordinary_publication_anchors SET output_path=CAST(output_path AS BLOB);")]
    [InlineData("UPDATE ordinary_publication_anchors SET output_size_bytes=13;")]
    [InlineData(@"UPDATE ordinary_publication_anchors SET output_path='C:\PRIVATE\wrong.mp4';")]
    [InlineData("UPDATE ordinary_publication_anchors SET observation_version=2;")]
    [InlineData("UPDATE ordinary_publication_anchors SET source='helper';")]
    [InlineData("UPDATE ordinary_recording_history SET status_code='failed';")]
    [InlineData("DELETE FROM schema_migrations WHERE version=30;")]
    public void CorruptedAnchorOrAssociationFailsWholeReadWithoutRepair(string sql)
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();f.Seed();f.Db.Corrupt(sql);var before=UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath);
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());f.NoProbes();Assert.Throws<UnifiedHistoryUnavailableException>(()=>f.Db.Page());Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath));
    }
    [Fact]
    public void GenuineV29MigrationAddsOnlyPrivateTableAndReceiptWithNoBackfill()
    {
        using var f=new WindowsReadOnlyFileMetadataNativeTests.Files();var path=Path.Combine(f.Root,"legacy.db");
        using(var c=new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();SqliteOutputPathCollation.Register(c);using var cmd=c.CreateCommand();foreach(var m in SqliteSchemaCatalog.Migrations.Where(m=>m.Version<=29)){cmd.CommandText=m.Definition;cmd.ExecuteNonQuery();cmd.CommandText="INSERT INTO schema_migrations VALUES($v,$n,$c,$at);";cmd.Parameters.AddWithValue("$v",m.Version);cmd.Parameters.AddWithValue("$n",m.Name);cmd.Parameters.AddWithValue("$c",m.Checksum);cmd.Parameters.AddWithValue("$at",1000+m.Version);cmd.ExecuteNonQuery();cmd.Parameters.Clear();}
            cmd.CommandText=@"INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,source_title,audio_mode,output_path,updated_at_utc) VALUES('legacy',1,0,'completed','display','display_capture','fixture','none','C:\legacy.mp4',0);";cmd.ExecuteNonQuery();
        }
        var before=UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:29);var store=new SqliteOperationalStore(path);store.Initialize();var after=UnifiedHistoryProfileFilterTests.RawSnapshot(path);Assert.Equal(before,UnifiedHistoryProfileFilterTests.RawSnapshot(path,schemaThrough:29));
        store.Initialize();Assert.Equal(after,UnifiedHistoryProfileFilterTests.RawSnapshot(path));using var read=store.OpenReadOnlyConnection();using var check=read.CreateCommand();check.CommandText="SELECT COUNT(*) FROM ordinary_publication_anchors;";Assert.Equal(0L,check.ExecuteScalar());SqliteSchemaV30.Validate(read,null);
    }
    [Fact]
    public void EmptyRetentionPageStillValidatesPrivateAnchorSchema()
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();f.Db.Execute("DROP TABLE ordinary_publication_anchors;");Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page());f.NoProbes();
    }
    [Fact]
    public void StorageRoundTripsMaximumUnsignedVolumeAndBothFullIdHalvesWithoutSignedIntegers()
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();var id=new ObservedFileIdentity(ulong.MaxValue,ulong.MaxValue,ulong.MaxValue);var row=f.Db.Recording("max",completed:true) with{OutputPath=f.PathFor()};row=row with{PublicationReceipt=PublicationReceipt.IssueForTests(row.OutputPath,12,id),BundleRegistration=new(null)};f.Db.Ordinary.Register(row);
        Assert.Equal(id,Assert.Single(f.Query.GetOrdinaryEvidencePage(Sid,Session,20,f.Before,null).Items).PublicationAnchor!.Identity);Assert.Equal(8,f.Db.Scalar("SELECT length(volume_id) FROM ordinary_publication_anchors;"));Assert.Equal(16,f.Db.Scalar("SELECT length(file_id) FROM ordinary_publication_anchors;"));
        var sample=RetentionHandleMetadataTests.Stable() with{Identity=id};f.Files.Observation=p=>new(FileObservationOutcome.Sampled,sample,sample,p,p);Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);
    }
    [Fact]
    public async Task TerminalRowAndAnchorAreObservedTogetherAcrossOneReadSnapshot()
    {
        using var f=RetentionPlannedOutputPathTests.Fixture();var completed=PublicationAnchorTestFixture.Anchored(f.Db.Recording("published",completed:true) with{OutputPath=f.PathFor(),BundleRegistration=new(null)});f.Db.Ordinary.Register(completed with{Status="preparing",CompletedAtUtc=null,StartedAtUtc=null,OutputSizeBytes=null,OutputDurationSeconds=null,PublicationReceipt=null,BundleRegistration=null});
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();f.Query.SnapshotEstablishedForTests=()=>{entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        var pending=Task.Run(()=>f.Page());try{Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));Assert.True(f.Db.Ordinary.TryPublish(completed));Assert.Equal("completed",f.Db.Ordinary.Read(completed.RecordingId)!.Status);}finally{release.Set();}
        Assert.Equal("not_completed",Assert.Single((await pending).Items).ReasonCode);f.NoProbes();f.Query.SnapshotEstablishedForTests=null;Assert.Equal("age_rule_match",Assert.Single(f.Page().Items).ReasonCode);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void PrivateIdentityCannotSerializeEvenWithPublicFieldsEnabled(bool fields)
    {
        var row=PublicationAnchorTestFixture.Anchored(new UnifiedHistoryTestDatabaseRow().Row);var meta=new OutputMeta{PublicationReceipt=row.PublicationReceipt};var json=new JsonSerializerOptions{IncludeFields=fields};var raw=JsonSerializer.Serialize(row,json)+JsonSerializer.Serialize(meta,json);
        foreach(var forbidden in new[]{"PublicationReceipt","PublicationAnchor","VolumeSerialNumber","IdLow","IdHigh","staging_copy_handle","fedcba"})Assert.DoesNotContain(forbidden,raw);
        Assert.Null(PublicationReceipt.Sign(new object(),row.PublicationReceipt!.Anchor));
        Assert.Empty(typeof(PublicationReceipt).GetConstructors(BindingFlags.Public|BindingFlags.Instance));
    }
    private sealed class UnifiedHistoryTestDatabaseRow
    {
        internal OrdinaryRecordingHistorySnapshot Row=>new("privacy",At,"completed","display","display_capture","fixture",15,"none",null,null,null,null,@"C:\Videos\private.mp4",null,null,null,null,null,null,At.AddSeconds(1),At.AddSeconds(16),"duration_reached",12,15);
    }
}
