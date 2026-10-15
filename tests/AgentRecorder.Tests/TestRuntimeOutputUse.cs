using AgentRecorder.Core;

namespace AgentRecorder.Tests;

// Explicit controlled empty claims for existing metadata/DB fixtures. Production
// composition must supply its actual RecordingEngine, never this test provider.
internal sealed class TestRuntimeOutputUse : IOrdinaryRuntimeOutputUseProvider
{
    internal static readonly TestRuntimeOutputUse None = new();
    public OrdinaryRuntimeOutputSnapshot ReadSnapshot(IReadOnlyList<string> paths)
        => new(paths.Select(path => new OrdinaryRuntimeOutputUse(path, false)));
}
