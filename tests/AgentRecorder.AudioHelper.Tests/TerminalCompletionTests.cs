using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using NAudio.Wave;
using Xunit;

namespace AgentRecorder.AudioHelper.Tests;

public partial class AudioHelperCaptureSessionTests
{
    // Gate the real file close after production has moved both resources into its terminal locals.
    // No production hook, altered business result, hardware, API or callback timer is required.
    private sealed class TerminalCloseStream(Stream inner) : Stream
    {
        internal readonly ManualResetEventSlim Entered=new(false),Release=new(false);
        internal bool FailClose;
        internal Action? BeforeClose;
        internal TimeSpan GateTimeout=TimeSpan.FromSeconds(6);
        private int _closed;
        public override bool CanRead=>inner.CanRead;
        public override bool CanSeek=>inner.CanSeek;
        public override bool CanWrite=>inner.CanWrite;
        public override long Length=>inner.Length;
        public override long Position {get=>inner.Position;set=>inner.Position=value;}
        public override void Flush()=>inner.Flush();
        public override int Read(byte[] b,int o,int n)=>inner.Read(b,o,n);
        public override void Write(byte[] b,int o,int n)=>inner.Write(b,o,n);
        public override long Seek(long o,SeekOrigin origin)=>inner.Seek(o,origin);
        public override void SetLength(long value)=>inner.SetLength(value);
        protected override void Dispose(bool disposing)
        {
            if(disposing && Interlocked.Exchange(ref _closed,1)==0)
            {
                BeforeClose?.Invoke();Entered.Set();
                if(!Release.Wait(GateTimeout))throw new TimeoutException("Fixture terminal gate was not released");
                inner.Dispose();
                if(FailClose)throw new IOException("Synthetic terminal close failure");
            }
            base.Dispose(disposing);
        }
    }
    private static Task<T> Dedicated<T>(Func<T> work)=>Task.Factory.StartNew(work,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    private static Task Dedicated(Action work)=>Task.Factory.StartNew(work,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);

    [Fact]
    public Task TerminalTransferMustNotLetDisposeReleaseRunBeforeResultPublication()=>TerminalTransferRace("dispose",false);
    [Theory]
    [InlineData("cancel",false)] [InlineData("repeat_dispose",false)] [InlineData("ok",false)]
    [InlineData("dispose",true)] [InlineData("cancel",true)] [InlineData("fault_wins",false)]
    [InlineData("owner_dispose",false)] [InlineData("dispose_timeout",false)]
    public Task TerminalTransferKeepsEventExitAndMediaConsistent(string mode,bool failClose)=>TerminalTransferRace(mode,failClose);
    private async Task TerminalTransferRace(string mode,bool failClose)
    {
        var dir=Path.Combine(Path.GetTempPath(),"terminal-transfer-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var output=Path.Combine(dir,"audio.wav");var partial=output+".partial";var signal=Path.Combine(dir,"stop");
        using var cts=new CancellationTokenSource();using var watcher=Watcher(signal,cts);
        var input=new FakeAudioInput{SourceKind=AudioSourceKind.SystemLoopback,Format=new WaveFormat(48000,32,2)};
        var options=Options("terminal-transfer",output,signal);options.SourceKind=AudioSourceKind.SystemLoopback;
        TerminalCloseStream? stream=null;
        var paths=new PathCheckResult{Ok=true,CanonicalPath=output,PartialPath=partial,OpenPartialStream=()=>stream=new TerminalCloseStream(new FileStream(partial,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)){FailClose=failClose}};
        var stdout=new StringWriter();using var session=new CaptureSession(options,paths,new EventWriter(stdout,null),watcher,cts,_=>(input,null,null));
        var run=Dedicated(session.Run);Task? terminal=null,dispose=null;
        try
        {
            Assert.True(SpinWait.SpinUntil(()=>GetPrivateLong(session,"_loopbackStartConfirmed")!=0,TimeSpan.FromSeconds(2)));
            long anchor=GetPrivateLong(session,"_firstSampleAnchorTicks");
            input.InjectPositionedPacket(Enumerable.Repeat((byte)0x11,3840).ToArray(),0,anchor);
            if(mode=="owner_dispose")stream!.BeforeClose=session.Dispose;
            if(mode=="dispose_timeout")stream!.GateTimeout=TimeSpan.FromSeconds(12);
            terminal=Dedicated(()=>{if(mode=="ok")input.StopRecording();else if(mode=="fault_wins")input.InjectError(new IOException("Original synthetic device failure"));else session.RequestStop();});
            Assert.True(stream!.Entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Null(typeof(CaptureSession).GetField("_input",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session));
            Assert.Null(typeof(CaptureSession).GetField("_writer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session));
            if(mode is "cancel" or "fault_wins")cts.Cancel();
            dispose=mode=="repeat_dispose"?Task.WhenAll(Enumerable.Range(0,12).Select(_=>Dedicated(session.Dispose))):Dedicated(session.Dispose);
            bool premature=await Task.WhenAny(run,Task.Delay(200))==run;
            File.WriteAllText(Path.Combine(dir,"before-release.json"),JsonSerializer.Serialize(new{premature,run.Status,exit=GetPrivateLong(session,"_exitCode"),outputExists=File.Exists(output),events=stdout.ToString()}));
            Assert.False(premature,"Run returned while the terminal owner was still closing its transferred writer");
            if(mode=="dispose_timeout")
            {
                // Verify the existing real eight-second bound. Extending a test's stream gate
                // cannot make Dispose forge completion, take the writer, or invalidate Run's waiter.
                await dispose.WaitAsync(TimeSpan.FromSeconds(9));Assert.False(run.IsCompleted);Assert.False(File.Exists(output));
                var completion=(TaskCompletionSource<int>)typeof(CaptureSession).GetField("_completed",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(session)!;
                Assert.False(completion.Task.IsCompleted);session.Dispose();Assert.False(completion.Task.IsCompleted);
            }
        }
        finally
        {
            stream?.Release.Set();
            foreach(var task in new Task?[]{terminal,dispose,run})
                if(task is not null)try{await task.WaitAsync(TimeSpan.FromSeconds(3));}catch(Exception ex){File.AppendAllText(Path.Combine(dir,"drain-errors.txt"),ex+"\n");}
            File.WriteAllText(Path.Combine(dir,"after-release.json"),JsonSerializer.Serialize(new{run.Status,exit=GetPrivateLong(session,"_exitCode"),outputExists=File.Exists(output),events=stdout.ToString()}));
        }
        bool failure=failClose || mode=="fault_wins";
        if(failure)
        {
            Assert.NotEqual(0,await run);Assert.False(File.Exists(output));Assert.False(File.Exists(partial));
            Assert.Contains("RESULT: FAIL",stdout.ToString());Assert.DoesNotContain("RESULT: STOPPED",stdout.ToString());Assert.DoesNotContain("RESULT: OK",stdout.ToString());
            Assert.Contains(mode=="fault_wins"?"Original synthetic device failure":"Synthetic terminal close failure",stdout.ToString());
        }
        else
        {
            Assert.Equal(0,await run);Assert.True(File.Exists(output));Assert.False(File.Exists(partial));
            using var wav=new WaveFileReader(output);Assert.True(wav.Length>=3840);var pcm=new byte[3840];Assert.Equal(pcm.Length,wav.Read(pcm,0,pcm.Length));Assert.All(pcm,b=>Assert.Equal(0x11,b));
            Assert.Contains(mode=="ok"?"RESULT: OK":"RESULT: STOPPED",stdout.ToString());Assert.DoesNotContain("RESULT: FAIL",stdout.ToString());
        }
        Assert.Equal(1,stdout.ToString().Split("RESULT: ").Skip(1).Count(x=>x.StartsWith("FAIL") || x.StartsWith("OK") || x.StartsWith("STOPPED")));
    }

    private sealed class TerminalFlushWriter : StringWriter
    {
        internal readonly ManualResetEventSlim Entered=new(false),Release=new(false);
        private bool _terminal;
        private int _gated;
        public override void WriteLine(string? text)
        {
            if(text is "RESULT: STOPPED" or "RESULT: OK" or "RESULT: FAIL")_terminal=true;
            base.WriteLine(text);
        }
        public override void Flush()
        {
            if(_terminal && Interlocked.Exchange(ref _gated,1)==0)
            {Entered.Set();if(!Release.Wait(TimeSpan.FromSeconds(4)))throw new TimeoutException("Fixture terminal event gate");}
            base.Flush();
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TerminalEventFlushMustFinishBeforeRunResultEvenWhenCancelledOrDisposed(bool fail)
    {
        var dir=Path.Combine(Path.GetTempPath(),"terminal-flush-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        using var cts=new CancellationTokenSource();using var watcher=Watcher(Path.Combine(dir,"stop"),cts);
        var input=new FakeAudioInput();var output=Path.Combine(dir,"audio.wav");using var events=new TerminalFlushWriter();
        using var session=new CaptureSession(Options("terminal-flush",output,Path.Combine(dir,"stop")),PathResult(output,output+".partial"),new EventWriter(events,null),watcher,cts,_=>(input,null,null));
        var run=Dedicated(session.Run);Task? terminal=null,dispose=null;
        try
        {
            Assert.True(SpinWait.SpinUntil(()=>input.Started,TimeSpan.FromSeconds(2)));input.InjectData(Enumerable.Repeat((byte)0x44,320).ToArray(),320);
            terminal=Dedicated(()=>{if(fail)input.InjectError(new IOException("Synthetic event-gated failure"));else session.RequestStop();});
            Assert.True(events.Entered.Wait(TimeSpan.FromSeconds(2)));cts.Cancel();dispose=Dedicated(session.Dispose);
            Assert.NotSame(run,await Task.WhenAny(run,Task.Delay(150)));Assert.Equal(!fail,File.Exists(output));
        }
        finally{events.Release.Set();}
        int result=await run.WaitAsync(TimeSpan.FromSeconds(3));await terminal!.WaitAsync(TimeSpan.FromSeconds(3));await dispose!.WaitAsync(TimeSpan.FromSeconds(3));
        if(fail){Assert.NotEqual(0,result);Assert.Contains("RESULT: FAIL",events.ToString());Assert.DoesNotContain("RESULT: STOPPED",events.ToString());}
        else{Assert.Equal(0,result);Assert.Contains("RESULT: STOPPED",events.ToString());Assert.DoesNotContain("RESULT: FAIL",events.ToString());}
        session.Dispose();
    }

    [Fact]
    public void DisposeBeforeRunIsAnExplicitClosedEntryRatherThanAnEmptyResourceGuess()
    {
        var dir=Path.Combine(Path.GetTempPath(),"never-run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        using var cts=new CancellationTokenSource();using var watcher=Watcher(Path.Combine(dir,"stop"),cts);int opens=0;
        var output=Path.Combine(dir,"audio.wav");var events=new StringWriter();
        var session=new CaptureSession(Options("never-run",output,Path.Combine(dir,"stop")),PathResult(output,output+".partial"),new EventWriter(events,null),watcher,cts,_=>{opens++;throw new Exception("must not open");});
        Parallel.For(0,16,_=>session.Dispose());session.Dispose();
        Assert.Throws<ObjectDisposedException>(()=>session.Run());Assert.Equal(0,opens);Assert.Equal("",events.ToString());Assert.False(File.Exists(output));Assert.False(watcher.Started);
    }

    [Theory]
    [InlineData("dispose")] [InlineData("cancel")]
    public async Task StartupOpenInFlightIsNotConfusedWithNeverRun(string mode)
    {
        var dir=Path.Combine(Path.GetTempPath(),"startup-open-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        using var cts=new CancellationTokenSource();using var watcher=Watcher(Path.Combine(dir,"stop"),cts);
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var input=new FakeAudioInput();int retired=0;input.OnDispose=_=>Interlocked.Increment(ref retired);
        var output=Path.Combine(dir,"audio.wav");var events=new StringWriter();
        using var session=new CaptureSession(Options("startup-open",output,Path.Combine(dir,"stop")),PathResult(output,output+".partial"),new EventWriter(events,null),watcher,cts,_=>
        {entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(3)));return(input,null,null);});
        var run=Dedicated(session.Run);Task? dispose=null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            if(mode=="cancel")cts.Cancel();
            dispose=Dedicated(session.Dispose);
            Assert.True(SpinWait.SpinUntil(()=>GetPrivateLong(session,"_userStopRequested")!=0,TimeSpan.FromSeconds(1)));
            Assert.NotSame(run,await Task.WhenAny(run,Task.Delay(100)));
        }
        finally{release.Set();}
        Assert.NotEqual(0,await run.WaitAsync(TimeSpan.FromSeconds(3)));await dispose!.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(input.Started);Assert.True(input.Disposed);Assert.Equal(1,retired);Assert.False(File.Exists(output));Assert.False(watcher.Started);
        Assert.Contains("RESULT: FAIL",events.ToString());Assert.DoesNotContain("RESULT: STOPPED",events.ToString());
    }

    [Theory]
    [InlineData("stream_open")] [InlineData("cancelled_start")] [InlineData("reentrant_dispose")]
    public async Task StartupPartialFailureOrReentrantDisposalRetiresWithoutPrematureSuccess(string mode)
    {
        var dir=Path.Combine(Path.GetTempPath(),"startup-failure-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        using var cts=new CancellationTokenSource();using var watcher=Watcher(Path.Combine(dir,"stop"),cts);
        var input=new FakeAudioInput();int retired=0;input.OnDispose=_=>Interlocked.Increment(ref retired);
        var output=Path.Combine(dir,"audio.wav");var events=new StringWriter();var paths=PathResult(output,output+".partial");
        if(mode=="stream_open")paths.OpenPartialStream=()=>throw new IOException("Synthetic startup reserve failure");
        if(mode=="cancelled_start")input.StartResult=StartRecordingResult.Cancelled;
        using var session=new CaptureSession(Options("startup-failure",output,Path.Combine(dir,"stop")),paths,new EventWriter(events,null),watcher,cts,_=>(input,null,null));
        if(mode=="reentrant_dispose")input.OnStartRecording=_=>session.Dispose();
        Assert.NotEqual(0,await Dedicated(session.Run).WaitAsync(TimeSpan.FromSeconds(3)));
        session.Dispose();session.Dispose();Assert.Equal(1,retired);Assert.False(File.Exists(output));Assert.Contains("RESULT: FAIL",events.ToString());Assert.DoesNotContain("RESULT: OK",events.ToString());
    }
}
