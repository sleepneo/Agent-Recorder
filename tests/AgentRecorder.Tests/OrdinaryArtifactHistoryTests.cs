using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryArtifactHistoryTests(ITestOutputHelper output)
{
    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task333",Guid.NewGuid().ToString("N")));
        internal readonly UnifiedHistoryTestDatabase Db;
        private readonly string _directory;
        internal SqliteOrdinaryRecordingHistoryStore Writer=>Db.Ordinary;
        internal OrdinaryRecordingHistorySnapshot Row=>Db.Recording("r",completed:true) with {OutputPath=Path.Combine(Root,_directory,"out.mp4")};
        internal Fixture(bool videos=false){_directory=videos?"Videos":"UserSelected";Db=new(Root);Directory.CreateDirectory(Path.GetDirectoryName(Row.OutputPath)!);}
        internal OrdinaryRecordingBundleOperation Seed(bool known=true,bool anchor=false,string state="ready")
        {
            var row=anchor?PublicationAnchorTestFixture.Anchored(Row):Row;
            var op=OrdinaryRecordingBundleOperation.Create("r",row.OutputPath);
            Writer.Register(row with {BundleRegistration=new(op)});
            if(state is "ready" or "failed")Complete(op,known,state);
            return op;
        }
        internal void Complete(OrdinaryRecordingBundleOperation op,bool known=true,string state="ready")
        {
            Assert.True(Writer.TryPublishBundle(new(op,new(state,op.BundlePath,state=="failed"?RecordingBundleErrorCodes.GenerationFailed:null,
                known&&state=="ready"?new(BundleContentsGenerationTests.Items()):null))));
            Assert.Equal(state,Writer.Read("r")!.Bundle!.Status);
        }
        internal OrdinaryArtifactCheckService Service(Ancestors a,Leaf l)=>new(Writer.ReadArtifactFacts,a,l);
        internal async Task RealBundle(bool anchor=true,bool known=true)
        {
            var ffmpeg=Path.Combine(TestHelper.ProjectRoot,"tools/ffmpeg/bin/ffmpeg.exe");
            var result=await new ExternalProcessRunner().RunAsync(ffmpeg,new[]{"-y","-f","lavfi","-i","color=c=black:s=64x64:r=15","-t","0.5","-c:v","libx264","-pix_fmt","yuv420p",Row.OutputPath},TimeSpan.FromSeconds(10));
            Assert.Equal(0,result.ExitCode);var size=new FileInfo(Row.OutputPath).Length;
            var row=Row with {OutputSizeBytes=size};if(anchor)row=PublicationAnchorTestFixture.Anchored(row,native:true);
            var op=OrdinaryRecordingBundleOperation.Create("r",row.OutputPath);Writer.Register(row with {BundleRegistration=new(op)});
            var request=new RecordingBundleRequest("r",null,"display","Synthetic",(0,0,64,64),"virtual_screen",DateTime.UtcNow.AddSeconds(-1),DateTime.UtcNow,
                1,0.5,15,"ffmpeg","duration_reached",false,"not_requested",null,null,null,null,null,null,row.OutputPath,"mp4","h264",64,64);
            var generated=await new FfmpegRecordingBundleGenerator().GenerateAsync(request);Assert.True(generated.Success);
            Assert.True(Writer.TryPublishBundle(new(op,new("ready",op.BundlePath,null,known?generated.ContentsEvidence:null))));
            Assert.Equal(known,Writer.Read("r")!.Bundle!.ContentsEvidence is not null);
        }
        public void Dispose(){Writer.Dispose();SqliteConnection.ClearAllPools();} // Preserve owned files and databases.
    }
    internal sealed class Ancestors : IReadOnlyPathMetadata
    {
        internal List<string> Calls=new();internal Func<string,FileAttributes> Read=_=>FileAttributes.Directory;
        public FileAttributes GetAttributes(string path){Calls.Add(path);return Read(path);}
        public long GetLength(string path)=>throw new InvalidOperationException("No separate length/content reads");
    }
    internal sealed class Leaf : IReadOnlyFileMetadataProbe
    {
        internal List<string> Calls=new();internal Func<string,FileMetadataObservation?> Read=path=>Sample(path,12);
        public FileMetadataObservation? Observe(string path){Calls.Add(path);return Read(path);}
    }
    internal static FileMetadataObservation Sample(string path,long size=12,ObservedFileIdentity? identity=null)
    {
        var s=new HandleMetadataSnapshot(size,4096,1,FileAttributes.Normal,0,0,133000000000000000,identity??new(1,2,3));
        return new(FileObservationOutcome.Sampled,s,s,path,path);
    }

    [Theory]
    [InlineData("preparing")][InlineData("recording")][InlineData("finalizing")][InlineData("failed")][InlineData("cancelled")]
    [InlineData("unknown_size")][InlineData("zero_size")][InlineData("not_video")]
    public async Task IneligibleMediaDoesNoFileIo(string kind)
    {
        using var f=new Fixture();var row=f.Row;
        row=kind switch {"unknown_size"=>row with {OutputSizeBytes=null},"zero_size"=>row with {OutputSizeBytes=0},
            "not_video"=>row with {OutputPath=Path.ChangeExtension(row.OutputPath,"png")},_=>f.Db.Recording("r") with {Status=kind,OutputPath=row.OutputPath}};
        f.Writer.Register(row);var a=new Ancestors();var l=new Leaf();var result=await f.Service(a,l).ReadAsync("r");
        Assert.Equal("complete",result!.ProbeState);Assert.Equal(kind is "unknown_size" or "zero_size"?"unknown":"not_applicable",result.Media.Observation);Assert.Null(result.Bundle.Contents);
        Assert.Empty(a.Calls);Assert.Empty(l.Calls);
    }

    [Theory]
    [InlineData("ready")][InlineData("generating")][InlineData("failed")][InlineData("interrupted_unknown")][InlineData("not_applicable")][InlineData("unrecorded")]
    public async Task UnknownBundleNeverGuessesAttachments(string state)
    {
        using var f=new Fixture();
        if(state=="interrupted_unknown")
        {f.Seed(state:"generating");Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));using var w=new SqliteOrdinaryRecordingHistoryStore(f.Db.Store);w.RecoverIncomplete(At.AddHours(1));}
        else if(state is "not_applicable" or "unrecorded")f.Writer.Register(state=="unrecorded"?f.Row:f.Row with {BundleRegistration=new(null)});
        else f.Seed(known:false,state:state);
        var a=new Ancestors();var l=new Leaf();var result=await f.Service(a,l).ReadAsync("r");
        Assert.Equal(state=="unrecorded"?null:state,result!.Bundle.HistoryStatus);Assert.Null(result.Bundle.Contents);Assert.Null(result.Bundle.ContentsEvidenceVersion);
        Assert.Single(l.Calls);Assert.Equal(f.Row.OutputPath,l.Calls[0]);
    }

    [Theory]
    [InlineData("UPDATE ordinary_publication_anchors SET file_id=zeroblob(16);")]
    [InlineData("UPDATE ordinary_publication_anchors SET output_size_bytes=13;")]
    [InlineData("DROP TABLE ordinary_publication_anchors;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='bad' WHERE version=30;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='bad' WHERE version=31;")]
    [InlineData("UPDATE schema_migrations SET definition_checksum='bad' WHERE version=33;")]
    [InlineData("DELETE FROM ordinary_bundle_contents;")]
    [InlineData("DELETE FROM ordinary_bundle_contents_items WHERE ordinal=2;")]
    [InlineData("UPDATE ordinary_bundle_contents SET bundle_path='C:\\foreign.bundle';")]
    [InlineData("UPDATE ordinary_recording_bundles SET operation_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';")]
    [InlineData("UPDATE ordinary_recording_history SET output_size_bytes=12.5;")]
    public async Task CorruptFrozenFactsFailBeforeAllFileIo(string sql)
    {
        using var f=new Fixture();f.Seed(anchor:true);f.Db.RawExecute("PRAGMA foreign_keys=OFF;PRAGMA ignore_check_constraints=ON;"+sql);
        var a=new Ancestors();var l=new Leaf();await Assert.ThrowsAsync<OrdinaryRecordingHistoryUnavailableException>(()=>f.Service(a,l).ReadAsync("r"));
        Assert.Empty(a.Calls);Assert.Empty(l.Calls);
    }

    [Fact]
    public async Task ReadBarrierFailurePrecedesSnapshotAndProbes()
    {
        using var f=new Fixture();f.Writer.Register(f.Row with {Status="preparing",StartedAtUtc=null,CompletedAtUtc=null,ReasonCode=null,OutputSizeBytes=null,OutputDurationSeconds=null});using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Writer.BeforePersistForTests=_=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        Assert.True(f.Writer.TryPublish(f.Row));await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));var a=new Ancestors();var l=new Leaf();
        try{await Assert.ThrowsAsync<OrdinaryRecordingHistoryUnavailableException>(()=>f.Service(a,l).ReadAsync("r"));Assert.Empty(a.Calls);Assert.Empty(l.Calls);}
        finally{release.Set();}Assert.Equal("completed",f.Writer.Read("r")!.Status);
    }

    [Fact]
    public async Task SnapshotBarrierKeepsMediaAnchorAndUnknownBundleTogether()
    {
        using var f=new Fixture();var op=f.Seed(anchor:true,state:"generating");using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Writer.ArtifactSnapshotEstablishedForTests=()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));};
        var a=new Ancestors();var l=new Leaf();
        l.Read=path=>Sample(path,12,new(0x123456789abcdef0,0xfedcba9876543210,0x876543210abcdef0));
        var pending=f.Service(a,l).ReadAsync("r");await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try{f.Complete(op);f.Db.RawExecute("PRAGMA ignore_check_constraints=ON;UPDATE ordinary_publication_anchors SET file_id=zeroblob(16);");}
        finally{release.Set();}
        var result=await pending;Assert.Equal("generating",result!.Bundle.HistoryStatus);Assert.Null(result.Bundle.Contents);Assert.Equal("match",result.Media.IdentityState);Assert.Single(l.Calls);
        f.Writer.ArtifactSnapshotEstablishedForTests=null;l.Calls.Clear();a.Calls.Clear();
        await Assert.ThrowsAsync<OrdinaryRecordingHistoryUnavailableException>(()=>f.Service(a,l).ReadAsync("r"));Assert.Empty(l.Calls);Assert.Empty(a.Calls);
        output.WriteLine("Same readonly SQLite snapshot: old media/anchor/generating despite concurrent ready and anchor damage; subsequent read fails before probing.");
    }
}
