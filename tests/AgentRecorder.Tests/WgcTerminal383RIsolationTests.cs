using AgentRecorder.Core;
using AgentRecorder.Capture;
using Xunit;
namespace AgentRecorder.Tests;
public sealed partial class OrdinaryWgcBundleTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task WgcTerminal383RLongConfigurationAndTwoShortPeersRetireIndependently(bool longFails)
    {
        using var env=new WindowBudgetEnvironment("3");using var f=IndependentWindow383Fixture();await f.Prepare();IndependentWindow383Providers(f);f.StartApi();
        var clips=new Dictionary<int,string>();
        foreach(int seconds in new[]{180,90,30}) {
            var clip=Path.Combine(f.Root,"prepared-"+seconds+".mp4");
            var ffmpeg=Path.Combine(TestHelper.ProjectRoot,"tools","ffmpeg","bin","ffmpeg.exe");
            var result=await new ExternalProcessRunner().RunAsync(ffmpeg,new[]{"-y","-f","lavfi","-i","color=c=blue:s=64x64:r=15","-t",seconds.ToString(),"-c:v","libx264","-pix_fmt","yuv420p",clip},TimeSpan.FromSeconds(30));
            Assert.Equal(0,result.ExitCode);clips[seconds]=clip;
        }
        f.SyntheticProcessCreatedForTests=process=>{var seconds=process.Options.DurationMs/1000;process.MediaClipOverride=clips[seconds];process.DurationMsOverride=seconds*1000;};
        var runs=new List<(Recording Rec,SyntheticProcess Process,WgcContinuousCaptureBackend Backend)>();
        try {
            foreach(var item in new[]{(42,180),(43,90),(44,30)}) {
                var q=IndependentWindow382Request(f,"window_"+item.Item1);q["stop_condition"]!["seconds"]=item.Item2;
                var rec=await NestedWindow381Accept(f,q);var live=await IndependentWindow382Approve(f,rec);
                Assert.Equal(item.Item2*1000,live.Process.Options.DurationMs);runs.Add((rec,live.Process,live.Backend));
            }
            f.Generator.Release.TrySetResult();
            for(int i=2;i>=1;i--) {
                runs[i].Process.Finish.TrySetResult();await Until(()=>runs[i].Rec.IsFinalized && runs[i].Rec.BundleSnapshot.Status == "not_applicable" && f.Writer.Read(runs[i].Rec.Id)?.Bundle?.Status=="not_applicable");
                await PathCanonical381RHistory(f,runs[i].Rec);Assert.Equal(RecState.recording,runs[0].Rec.State);
            }
            runs[0].Process.FailMedia=longFails;runs[0].Process.Finish.TrySetResult();await Until(()=>runs[0].Rec.IsFinalized);
            Assert.Equal(longFails?RecState.failed:RecState.completed,runs[0].Rec.State);
            foreach(var peer in runs.Skip(1)) {Assert.Equal(RecState.completed,peer.Rec.State);Assert.Equal(Hash(clips[peer.Rec.DurationSeconds!.Value]),Hash(peer.Rec.OutputPath));}
            await Task.WhenAll(runs.Select(x=>x.Backend.OutputRetirement)).WaitAsync(TimeSpan.FromSeconds(5));
            await Until(()=>f.Engine.GetIndependentWindowBudget().Reserved==0);
            SaveSeriesBundleEvidence("terminal383r-before-cleanup-"+longFails,new {runs=runs.Select(x=>new {id=x.Rec.Id,state=x.Rec.State.ToString(),x.Rec.Error,bundle=x.Rec.BundleSnapshot.Status,retired=x.Backend.OutputRetirement.IsCompleted,begin=x.Process.BeginObserved,exited=x.Process.HasExited}).ToArray()});
        } catch(Exception error) { SaveSeriesBundleEvidence("terminal383r-isolation-first-"+longFails,new {error=error.ToString(),runs=runs.Select(x=>new {id=x.Rec.Id,state=x.Rec.State.ToString(),x.Rec.Error,bundle=x.Rec.BundleSnapshot.Status,retired=x.Backend.OutputRetirement.IsCompleted,begin=x.Process.BeginObserved,exited=x.Process.HasExited}).ToArray()});throw; } finally {await NestedWindow381Drain(f);}
    }
}
