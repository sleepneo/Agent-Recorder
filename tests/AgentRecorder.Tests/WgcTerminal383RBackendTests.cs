using AgentRecorder.Capture;
using Xunit;
namespace AgentRecorder.Tests;
public sealed partial class WgcContinuousCaptureBackendTests
{
    [Fact]
    public async Task WgcTerminal383RUnconfirmedExitCannotPublishOrRetireOutputEvenAfterDispose()
    {
        var path=Path.Combine(_finalDir,"unknown383r.mp4");
        var fake=new FakeSession(CreateOptionsWithHelperPath());fake.StartTcs.TrySetResult(null);fake.AuthorizeTcs.TrySetResult(true);
        var backend=CreateBackend(_=>fake,out var publisher,out _);
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);backend.OnNaturalExit((_,_)=>done.TrySetResult());
        CaptureAuthorizationTestHelper.StartWithSyntheticConsumedProof(backend,CreateValidConfig(path));
        fake.DefaultResult.ProcessExitConfirmed=false;fake.CompletionTcs.TrySetResult(fake.DefaultResult);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,publisher.CallCount);Assert.False(File.Exists(path));
        Assert.False(backend.OutputRetirement.IsCompleted);backend.Dispose();Assert.False(backend.OutputRetirement.IsCompleted);
    }
}
