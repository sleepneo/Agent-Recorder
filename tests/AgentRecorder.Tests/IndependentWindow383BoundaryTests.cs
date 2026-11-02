using AgentRecorder.Capture;
using Xunit;
namespace AgentRecorder.Tests;
public sealed class IndependentWindow383BoundaryTests
{
    [Theory][InlineData("known",true)][InlineData("cleanup_unknown",false)][InlineData("launch_unknown",false)]
    public void PublishedNoResourceFactRequiresSealedSuccessfulKnownCleanup(string kind,bool expected)
    {
        var boundary=new WorkerStartBoundary();Assert.False(boundary.NoResourcesRetired.IsCompleted);
        if(kind=="launch_unknown")Assert.Throws<IOException>(()=>boundary.Start(a=>a.Launch(()=>throw new IOException("owned unknown launch"),false),()=>false));
        boundary.Close(()=>kind!="cleanup_unknown");
        Assert.Equal(expected,boundary.NoResourcesRetired.IsCompletedSuccessfully);
        Assert.Equal(expected,boundary.ObserveRetired(()=>false));
        Assert.Throws<ObjectDisposedException>(()=>boundary.Start(_=>{},()=>false));
    }
}
