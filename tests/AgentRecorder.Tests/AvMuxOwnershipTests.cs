using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class AvMuxOwnershipTests
{
    internal sealed class PausedMux(bool pause, string outcome) : IExternalProcessRunner
    {
        private readonly ExternalProcessRunner _actual = new();
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Partial;
        public async Task<ExternalProcessResult> RunAsync(string executable,IReadOnlyList<string> args,TimeSpan timeout,
            bool captureStderr=true,Encoding? stderrEncoding=null,CancellationToken cancellationToken=default)
        {
            var result=await _actual.RunAsync(executable,args,timeout,captureStderr,stderrEncoding,cancellationToken);
            if(args.Contains("-filter_complex"))
            {
                Assert.Equal(0,result.ExitCode);Partial=args[^1];Assert.True(File.Exists(Partial));Written.TrySetResult();
                if(pause)await Release.Task.WaitAsync(TimeSpan.FromSeconds(20),cancellationToken);
                if(outcome=="fail")return new(1,false,"owned injected failure after actual mux");
                if(outcome=="cancel")throw new OperationCanceledException("owned injected cancellation after actual mux");
            }
            return result;
        }
    }

    internal static string Ffmpeg => Path.Combine(TestHelper.ProjectRoot,"tools/ffmpeg/bin/ffmpeg.exe");
    internal static void AssertNoOwnedMux(string output)
    {
        // Only this approved final's internal 32-character nonce namespace.
        // This asserts absence; it never selects a partial for a test to use.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!,Path.GetFileName(output)+"."+new string('?',32)+".muxing.partial.mp4"));
    }
    internal static async Task Generate(string path,string source,bool audio)
    {
        Assert.True(File.Exists(Ffmpeg));
        var args=new List<string>{"-y","-f","lavfi","-i",source,"-t",audio ? "4" : "1"};
        args.AddRange(audio ? new[]{"-c:a","pcm_s16le"} : new[]{"-c:v","libx264","-pix_fmt","yuv420p"});args.Add(path);
        var result=await new ExternalProcessRunner().RunAsync(Ffmpeg,args,TimeSpan.FromSeconds(30));Assert.True(result.ExitCode==0,result.Stderr);
    }
    private static async Task<byte[]> Decode(string path,params string[] options)
    {
        var info=new ProcessStartInfo(Ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-v","error","-i",path}.Concat(options).Append("pipe:1"))info.ArgumentList.Add(arg);
        using var process=Process.Start(info)!;using var data=new MemoryStream();
        var stderr=process.StandardError.ReadToEndAsync();var copy=process.StandardOutput.BaseStream.CopyToAsync(data);
        await Task.WhenAll(copy,process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(15));Assert.True(process.ExitCode==0,await stderr);
        return data.ToArray();
    }
    internal static async Task VerifyBlue440(string path)
    {
        var rgb=await Decode(path,"-map","0:v:0","-frames:v","1","-f","rawvideo","-pix_fmt","rgb24");
        Assert.Equal(64*64*3,rgb.Length);Assert.True(rgb[2]>200 && rgb[0]<30 && rgb[1]<30,"Outer must retain blue video, not inner red");
        var pcm=await Decode(path,"-map","0:a:0","-t","0.8","-f","s16le","-ac","1","-ar","48000");
        var crossings=0;for(var i=2;i<pcm.Length;i+=2)if(BitConverter.ToInt16(pcm,i-2)<=0 && BitConverter.ToInt16(pcm,i)>0)crossings++;
        var seconds=pcm.Length/2d/48000;Assert.InRange(crossings/seconds,430,450);
    }

    [Theory]
    [InlineData(false,"fail")] [InlineData(true,"fail")]
    [InlineData(false,"cancel")] [InlineData(true,"cancel")]
    public async Task InterleavedRealMuxFailureCannotOverwriteOrDeleteOtherOperation(bool shared,string outcome)
    {
        var root=Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task335r",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
        var outerRunner=new PausedMux(true,"success");Task<AvFinalizer.Result>? outerTask=null;
        try
        {
            var blue=Path.Combine(root,"blue.mp4");var red=Path.Combine(root,"red.mp4");var low=Path.Combine(root,"440.wav");var high=Path.Combine(root,"880.wav");
            await Generate(blue,"color=c=blue:s=64x64:r=15",false);await Generate(red,"color=c=red:s=64x64:r=15",false);
            await Generate(low,"sine=frequency=440:sample_rate=48000",true);await Generate(high,"sine=frequency=880:sample_rate=48000",true);
            var output=Path.Combine(root,"outer.mp4");var innerOutput=shared ? output : Path.Combine(root,"inner.mp4");
            var oldFixed=output+".muxing.partial.mp4";File.WriteAllText(oldFixed,"UNOWNED-SENTINEL");
            outerTask=new AvFinalizer(outerRunner).FinalizeAsync(blue,low,output,TimeSpan.FromSeconds(.1),AudioCaptureSourceKind.SystemLoopback,false);
            await outerRunner.Written.Task.WaitAsync(TimeSpan.FromSeconds(15));var partial=Assert.IsType<string>(outerRunner.Partial);var hash=SHA256.HashData(File.ReadAllBytes(partial));
            var innerRunner=new PausedMux(false,outcome);var innerTask=new AvFinalizer(innerRunner).FinalizeAsync(red,high,innerOutput,TimeSpan.FromSeconds(.1),AudioCaptureSourceKind.SystemLoopback,false);
            if(outcome=="cancel")await Assert.ThrowsAsync<OperationCanceledException>(()=>innerTask);
            else { var failed=await innerTask;Assert.Equal(1,failed.ExitCode);Assert.NotNull(failed.Error);Assert.Null(failed.Meta.PublicationReceipt); }
            Assert.NotEqual(partial,innerRunner.Partial);Assert.Equal(Path.GetDirectoryName(output),Path.GetDirectoryName(partial));
            Assert.False(File.Exists(innerRunner.Partial));Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(partial)));
            Assert.Equal("UNOWNED-SENTINEL",File.ReadAllText(oldFixed));
            outerRunner.Release.TrySetResult();var result=await outerTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(result.Error);Assert.Equal(0,result.ExitCode);Assert.NotNull(result.Meta.PublicationReceipt);
            Assert.True(result.Meta.OutputFileExists);Assert.False(File.Exists(partial));await VerifyBlue440(output);
        }
        finally
        {
            outerRunner.Release.TrySetResult();if(outerTask is not null)try { await outerTask.WaitAsync(TimeSpan.FromSeconds(20)); }catch { }
            Assert.StartsWith(Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot,".local-data/temp/task335r"))+Path.DirectorySeparatorChar,root);Directory.Delete(root,true);
        }
    }
}
