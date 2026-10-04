using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class AppInteractiveDesktopStartupTests
{
    [Fact]
    public void IsolatedDesktopRejectionOccursBeforeTheRuntimeCanAcquireMutexOrBindApi()
    {
        var observation = new InteractiveDesktopObservation(
            123, 7, "S-1-5-21-user", 7, "S-1-5-21-user",
            "WinSta0", "CodexSandboxDesktop", "Default", "INTERACTIVE_DESKTOP_REQUIRED");
        var runtimeEntered = false;

        var started = AgentRecorder.App.Program.TryStartRuntimeOnlyOnVerifiedDesktop(
            observation, () => runtimeEntered = true);

        Assert.False(started);
        Assert.False(runtimeEntered);
    }

    [Fact]
    public void VerifiedDesktopEntersRuntimeExactlyOnce()
    {
        var observation = new InteractiveDesktopObservation(
            123, 7, "S-1-5-21-user", 7, "S-1-5-21-user",
            "WinSta0", "Default", "Default", string.Empty);
        var runtimeEntries = 0;

        var started = AgentRecorder.App.Program.TryStartRuntimeOnlyOnVerifiedDesktop(
            observation, () => runtimeEntries++);

        Assert.True(started);
        Assert.Equal(1, runtimeEntries);
    }
}
