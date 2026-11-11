using AgentRecorder.Core;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("HeadlessHostIntegration")]
public sealed class ConsentInvariantFixtureRestorationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelperDelegatesRestoreAfterNormalOrFailedTest(bool failAfterRequest)
    {
        var originalPath = RecordingPreflightChecker.AudioHelperPathResolver;
        var originalProbe = RecordingPreflightChecker.AudioHelperProbeRunner;
        var fixture = new ConsentInvariantTests();
        var failure = new InvalidOperationException("synthetic consent test failure");
        try
        {
            var actual = await Record.ExceptionAsync(async () =>
            {
                using (fixture)
                {
                    Assert.NotSame(originalPath, RecordingPreflightChecker.AudioHelperPathResolver);
                    Assert.NotSame(originalProbe, RecordingPreflightChecker.AudioHelperProbeRunner);
                    await fixture.CreateRecording_SystemAudioEnabled_RequiresConsentBeforeBackend();
                    if (failAfterRequest) throw failure;
                }
            });
            if (failAfterRequest) Assert.Same(failure, actual);
            else Assert.Null(actual);
            Assert.Same(originalPath, RecordingPreflightChecker.AudioHelperPathResolver);
            Assert.Same(originalProbe, RecordingPreflightChecker.AudioHelperProbeRunner);

            using var later = new ConsentInvariantTests();
            var laterPath = RecordingPreflightChecker.AudioHelperPathResolver;
            var laterProbe = RecordingPreflightChecker.AudioHelperProbeRunner;
            fixture.Dispose();
            Assert.Same(laterPath, RecordingPreflightChecker.AudioHelperPathResolver);
            Assert.Same(laterProbe, RecordingPreflightChecker.AudioHelperProbeRunner);
        }
        finally
        {
            fixture.Dispose();
            RecordingPreflightChecker.AudioHelperPathResolver = originalPath;
            RecordingPreflightChecker.AudioHelperProbeRunner = originalProbe;
        }
    }
}
