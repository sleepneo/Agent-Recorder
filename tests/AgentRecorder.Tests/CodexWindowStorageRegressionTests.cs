using AgentRecorder.Capture;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class CodexWindowStorageRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateProbeMustNotThrowAfterFinalPublicationWins(bool throws)
    {
        var provider = new FakeStorageProvider();
        var paths = new CaptureWritePaths(@"D:\output", null, @"D:\temp\AgentRecorder");
        using var guard = new WindowStorageSafety(paths, 60, provider);
        guard.EnsureAdmission();
        provider.Query = (_, _) =>
        {
            // Model publication winning while a capacity query is in flight.
            Assert.True(guard.TryCommit(() => { }, true));
            if (throws) throw new IOException("late capacity query failed");
            return Task.FromResult<IReadOnlyList<StorageVolumeSample>>(Array.Empty<StorageVolumeSample>());
        };

        var exception = Record.Exception(() => guard.EnsureRuntimeCapacity());
        Assert.True(guard.Committed);
        Assert.Null(guard.FailureCode);
        Assert.Null(exception);
    }
}
