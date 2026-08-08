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

/// <summary>
/// Legacy serial collection for tests that directly mutate the injectable
/// SystemQuery display/window providers. The provider implementation is scoped
/// by AsyncLocal, so unrelated readers cannot observe a sibling test's fake;
/// this collection still serializes its direct writer members and documents the
/// intended writer boundary. API/headless writers use their own global port
/// collection, which is also a compatible DisableParallelization boundary.
/// </summary>
[CollectionDefinition("NonParallel-SystemQueryProviders")]
public sealed class NonParallelSystemQueryProvidersCollection
{
}

/// <summary>
/// Collection for tests that mutate <c>AGENT_RECORDER_WINDOW_BACKEND</c>. Members
/// never execute in parallel with each other so feature-flag state does not leak.
/// </summary>
[CollectionDefinition("NonParallel-WindowBackend", DisableParallelization = true)]
public sealed class NonParallelWindowBackendCollection
{
}

/// <summary>
/// Collection for tests that mutate the injectable DWM composition flush used by
/// <see cref="AgentRecorder.App.DwmCompositionBarrier"/>. Members never execute in
/// parallel so static override state does not leak between tests.
/// </summary>
[CollectionDefinition("NonParallel-DwmCompositionBarrier", DisableParallelization = true)]
public sealed class NonParallelDwmCompositionBarrierCollection
{
}

/// <summary>
/// Shared isolation boundary for every test class that launches real
/// PowerShell/ping/compiled-helper process trees:
/// <see cref="WgcHelperProcessRunnerTests"/>,
/// <see cref="WgcContinuousManagedSessionTests"/> (real process-tree fixture
/// test), and <see cref="WgcContinuousCaptureBackendRealProcessTests"/>.
/// Members never execute in parallel WITH EACH OTHER, so at most one real
/// process-tree fixture is alive at a time and fixtures cannot contend for
/// process startup, PID files, or kill-tree timing. The collection definition
/// deliberately does NOT set DisableParallelization: these classes contain no
/// process-wide mutable state, so the collection may still run in parallel
/// with fake-only classes in other collections, keeping full-suite wall time
/// bounded. Assignment is explicit on each class; new classes that spawn real
/// process trees must join this collection.
/// </summary>
[CollectionDefinition("NonParallel-RealProcess")]
public sealed class NonParallelRealProcessCollection
{
}

/// <summary>
/// Collection for tests that mutate any process-scoped AgentRecorder
/// environment variable other than <c>AGENT_RECORDER_DATA_DIR</c> (handled
/// separately): <c>AGENT_RECORDER_CAPTURE_BACKEND</c>,
/// <c>AGENT_RECORDER_TEST_MODE</c>, and
/// <c>AGENT_RECORDER_AUDIOHELPER_PATH</c>, and
/// <c>AGENT_RECORDER_FFMPEG_DIR</c>. Members never execute in parallel
/// with each other so env-var state cannot leak between concurrently running
/// tests. Without this boundary, parallel runs see spurious failures:
/// <list type="bullet">
/// <item>ConfigParser reads <c>AGENT_RECORDER_TEST_MODE</c> mid-run and gets a
/// transient value set by a sibling test, skipping display enumeration when a
/// real-feature test expected it.</item>
/// <item>AvWorkerFactory.GetBackend reads a partially-overwritten backend
/// override and instantiates the wrong capture stack.</item>
/// <item>Path resolution reads <c>AGENT_RECORDER_AUDIOHELPER_PATH</c> after a
/// sibling test has reset it and falls back to an unexpected default
/// layout.</item>
/// <item>FFmpeg locator tests invalidate the process-wide locator while a
/// capture test is resolving the configured executable.</item>
/// </list>
/// </summary>
[CollectionDefinition("NonParallel-AgentRecorderEnvVar", DisableParallelization = true)]
public sealed class NonParallelAgentRecorderEnvVarCollection
{
}
