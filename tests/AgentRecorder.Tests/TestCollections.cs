using Xunit;

namespace AgentRecorder.Tests;

/// <summary>
/// Collection for tests that mutate <c>AGENT_RECORDER_DATA_DIR</c> (a
/// process-scoped environment variable). Members of this collection never
/// execute in parallel with each other so each test can safely redirect
/// audit log output to a unique temp directory and restore the original
/// value afterwards.
/// </summary>
[CollectionDefinition("NonParallel-AgentRecorderDataDir", DisableParallelization = true)]
public sealed class NonParallelAgentRecorderDataDirCollection
{
}
