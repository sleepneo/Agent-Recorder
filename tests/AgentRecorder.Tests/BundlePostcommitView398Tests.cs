using System.Text;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;
[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class BundlePostcommitView398Tests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("C")][InlineData("D")]
    public async Task RealLavfiMustPublishFiveBoundAttachmentsAndSurviveHistoryRestart(string volume)
    {
        var parent=volume=="C" ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Temp")
            : Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task398");
        var root=Path.GetFullPath(Path.Combine(parent,"AgentRecorder-task398-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        output.WriteLine("ownedRoot="+root);
        using var db=new UnifiedHistoryTestDatabase(root);
        Directory.CreateDirectory(Path.Combine(root,"Videos"));
        var media=Path.Combine(root,"Videos","Long synthetic main video.mp4");
        var ffmpeg=Path.Combine(TestHelper.ProjectRoot,"tools/ffmpeg/bin/ffmpeg.exe");
        var run=await new ExternalProcessRunner().RunAsync(ffmpeg,new[]{"-y","-f","lavfi","-i","color=c=blue:s=64x64:r=15","-t","1","-c:v","libx264","-pix_fmt","yuv420p",media},TimeSpan.FromSeconds(15));
        Assert.Equal(0,run.ExitCode);Assert.False(run.TimedOut);
        var initial=File.ReadAllBytes(media);
        var observed=WindowsReadOnlyFileMetadataProbe.Instance.ObserveRetention(media);
        Assert.Equal(FileObservationOutcome.Sampled,observed.Outcome);
        var op=OrdinaryRecordingBundleOperation.Create("owned",media);
        var row=db.Recording("owned",completed:true) with { OutputPath=media,OutputSizeBytes=initial.Length,BundleRegistration=new(op) };
        db.Ordinary.Register(row);
        var stages=new List<string>();
        var publisher=new BundlePublicationReceipt.Publisher { StageForTests=(stage,_)=>
        { stages.Add(stage);if(stage=="before_commit")Assert.False(Directory.Exists(op.BundlePath));
          if(stage=="after_commit")Assert.Equal(5,Directory.GetFiles(op.BundlePath).Length); } };
        var generator=new FfmpegRecordingBundleGenerator(ffmpegPathProvider:()=>ffmpeg) { PublisherFactory=()=>publisher };
        var result=await generator.GenerateAsync(BundlePublicationIdentityTests.RequestFor(media,op.RecordingId,op.OperationId));
        var facts=new { root,volume,filesystem=new DriveInfo(Path.GetPathRoot(root)!).DriveFormat,observed.ShortLeaf,observed.DataRootShortLeaf,
            stages,result.Success,result.ErrorCode,result.ErrorDetail,publisher.Cleanup,
            finalFiles=Directory.Exists(op.BundlePath)?Directory.GetFiles(op.BundlePath).Select(Path.GetFileName).ToArray():Array.Empty<string>(),
            remaining=Directory.GetFiles(root,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(root,p)).ToArray(),
            mainPreserved=initial.SequenceEqual(File.ReadAllBytes(media)),mainIdentity=observed.After!.Identity };
        var json=JsonSerializer.Serialize(facts);output.WriteLine(json);
        // Test DB disposal removes its owned tree; preserve diagnostic evidence outside it.
        var evidence=Path.Combine(parent,Path.GetFileName(root)+"-evidence.json");File.WriteAllText(evidence,json);output.WriteLine("evidence="+evidence);
        Assert.True(facts.mainPreserved);Assert.True(result.Success,result.ErrorDetail);Assert.NotNull(result.PublicationReceipt);
        var receipt=result.PublicationReceipt!;
        Assert.Equal(5,receipt.Evidence.Anchors.Count);
        foreach(var anchor in receipt.Evidence.Anchors)
        {var now=WindowsReadOnlyFileMetadataProbe.Instance.Observe(anchor.Path);Assert.Equal(anchor.Identity,now.After!.Identity);Assert.Equal(anchor.Length,now.After.Length);}
        var snapshot=new OrdinaryRecordingBundleSnapshot("ready",op.BundlePath,null,result.ContentsEvidence) { IdentityEvidence=receipt.Evidence };
        Assert.True(db.Ordinary.TryPublishBundle(new(op,snapshot)));Assert.Equal(snapshot,db.Ordinary.Read("owned")!.Bundle);
        using var restarted=new SqliteOrdinaryRecordingHistoryStore(db.Store);Assert.Equal(snapshot,restarted.Read("owned")!.Bundle);
        var check=await new OrdinaryArtifactCheckService(restarted).ReadAsync("owned");Assert.All(check!.Bundle.Contents!,i=>Assert.Equal("match",i.IdentityState));
        var changed=receipt.Evidence.Anchors[0];File.Move(changed.Path,changed.Path+".original");File.WriteAllBytes(changed.Path,new byte[changed.Length]);
        check=await new OrdinaryArtifactCheckService(restarted).ReadAsync("owned");Assert.Equal("mismatch",check!.Bundle.Contents![0].IdentityState);
    }
}
