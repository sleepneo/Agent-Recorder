using AgentRecorder.Capture;
using AgentRecorder.Core;
using Xunit;
using static AgentRecorder.Tests.RuntimeOutputTestEngine;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private static Fixture RuntimeFixture() => new(testRoot: Path.Combine(Path.GetTempPath(), "bundle-runtime"));
    private static void RegisterOtherOutput(Fixture f, string name = "other")
    {
        var path = Path.Combine(f.Root, name + ".mp4");
        Claim(f.Engine, new Recording(false) { IsOrdinaryLocalRequest = true, OutputPath = path }, path);
    }


}
