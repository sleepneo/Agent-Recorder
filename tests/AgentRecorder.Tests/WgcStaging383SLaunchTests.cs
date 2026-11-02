using AgentRecorder.Capture;
using Xunit;
namespace AgentRecorder.Tests;
public sealed partial class WgcContinuousManagedSessionTests
{
    [Fact]
    public async Task Staging383SDisposeBeforeStartSealsNoResourcesAgainstLateLaunch()
    {
        var options=CreateOptions("sealed383s");var process=new FakeWgcContinuousProcess(Array.Empty<string>());
        using var session=new WgcContinuousManagedSession(options,process);
        session.Dispose();await session.ProcessRetirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(()=>session.StartAsync());Assert.Equal(0,process.StartInvocationCount);
        await session.CompletionTask.WaitAsync(TimeSpan.FromSeconds(15));Assert.True(session.ProcessRetirement.IsCompletedSuccessfully);
    }
    [Fact]
    public async Task Staging383SLaunchingOwnerCannotPublishNoResourcesBeforeLateChildRetires()
    {
        var options=CreateOptions("late-launch383s");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process=new FakeWgcContinuousProcess(Array.Empty<string>(),startEntered:entered,startRelease:release);
        using var session=new WgcContinuousManagedSession(options,process);
        var start=Task.Run(()=>session.StartAsync());
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(session.Dispose).WaitAsync(TimeSpan.FromSeconds(7));
            Assert.False(session.ProcessRetirement.IsCompleted); // Start owns a possible child, not NoResources.
            release.TrySetResult();await start.WaitAsync(TimeSpan.FromSeconds(5));
            await session.ProcessRetirement.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(process.HasExited);
        } finally {release.TrySetResult();await start.WaitAsync(TimeSpan.FromSeconds(10));}
    }
}
