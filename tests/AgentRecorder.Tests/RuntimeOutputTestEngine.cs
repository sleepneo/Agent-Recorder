using System.Collections;
using System.Reflection;
using AgentRecorder.Core;

namespace AgentRecorder.Tests;

// Test-only access to the existing admission/release operations and lock. No
// production mutation seam is added for the read provider.
internal static class RuntimeOutputTestEngine
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static object OutputLock(RecordingEngine engine)
        => typeof(RecordingEngine).GetField("_ordinaryOutputLock", Private)!.GetValue(engine)!;
    internal static int OwnerCount(RecordingEngine engine)
        => ((IDictionary)typeof(RecordingEngine).GetField("_ordinaryOutputs", Private)!.GetValue(engine)!).Count;
    internal static void Claim(RecordingEngine engine, Recording recording, string path)
        => typeof(RecordingEngine).GetMethod("ClaimOrdinaryOutput", Private)!.Invoke(engine, [recording, path, null]);
    // Deliberately impossible production state: duplicated paths with 500/501
    // distinct owners test bounded read-side uncertainty, not legal admission.
    internal static void InjectSyntheticClaimForBound(RecordingEngine engine, Recording recording, string path)
    {
        var type = typeof(RecordingEngine).GetNestedType("OrdinaryOutputOwner", BindingFlags.NonPublic)!;
        var owner = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { recording, path, false, path }, null)!;
        lock (OutputLock(engine))
            ((IDictionary)typeof(RecordingEngine).GetField("_ordinaryOutputs", Private)!.GetValue(engine)!)[recording.Id] = owner;
    }
    internal static void Release(RecordingEngine engine, Recording recording)
        => typeof(RecordingEngine).GetMethod("ReleaseUnregisteredOrdinaryOutput", Private)!.Invoke(engine, [recording]);
    internal static OrdinaryRuntimeOutputSnapshot? Read(RecordingEngine engine, params string[] paths)
        => ((IOrdinaryRuntimeOutputUseProvider)engine).ReadSnapshot(Array.AsReadOnly(paths));
}
