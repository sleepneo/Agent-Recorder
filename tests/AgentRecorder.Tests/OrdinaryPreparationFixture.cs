using System.Collections;
using System.Reflection;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

internal sealed class OrdinaryPreparationFixture : IDisposable
{
    internal readonly OriginalFileScopeFixture Files = new();
    internal readonly RecordingEngine Engine = new(new RetentionSharedOutputPathTests.QuietAudit());
    internal PublicationIdentityAnchor Anchor => Files.Anchor;
    internal OriginalFileScopeFixture.Native Api => Files.Api;
    internal OrdinaryRetentionPreparationService Service(TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? deadline = null, Func<Action, Task>? dispatch = null)
    {
        Api.Anchor = Anchor;
        return Engine.CreateRetentionPreparationService(Files.Root, Api, Files.Admission, clock, deadline, dispatch);
    }
    internal static Recording Recording(string path, string backend = "ffmpeg", string audio = "none")
    {
        var rec = new Recording
        {
        IsOrdinaryLocalRequest = true, OutputPath = path, BackendType = backend,
        AudioSourceKind = audio == "none" ? AudioCaptureSourceKind.None : audio == "microphone" ? AudioCaptureSourceKind.Microphone : AudioCaptureSourceKind.SystemLoopback,
        Config = new() { Mode = "video", SourceKind = backend.StartsWith("wgc", StringComparison.Ordinal) ? "window" : "display", OutputPath = path,
            AudioSourceKind = audio == "none" ? AudioCaptureSourceKind.None : audio == "microphone" ? AudioCaptureSourceKind.Microphone : AudioCaptureSourceKind.SystemLoopback }
        };
        rec.ApprovedCapturePlan = new("default", backend, new("default", backend, "fixture", "fresh_probe", 0, false),
            rec.Config.SourceKind == "window" ? "window_surface" : "display_surface", rec.Config.SourceKind, null, 0, null);
        return rec;
    }
    internal static void Claim(RecordingEngine engine, Recording rec, string path, string? requested = null)
    {
        try { typeof(RecordingEngine).GetMethod("ClaimOrdinaryOutput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [rec, path, requested]); }
        catch (TargetInvocationException e) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); }
    }
    internal static void AssertConflict(RecordingEngine engine, string path, string backend = "ffmpeg", string audio = "none", string? requested = null)
    {
        var error = Assert.Throws<ApiException>(() => Claim(engine, Recording(path, backend, audio), path, requested));
        Assert.Equal("OUTPUT_PATH_IN_USE", error.Code); Assert.Equal(409, error.Status);
    }
    internal static void Corrupt(RecordingEngine engine, Recording rec, string? path, bool nullOwner = false)
    {
        var type = typeof(RecordingEngine).GetNestedType("OrdinaryOutputOwner", BindingFlags.NonPublic)!;
        var owner = nullOwner ? null : Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [rec, path, false], null);
        var dict = (IDictionary)typeof(RecordingEngine).GetField("_ordinaryOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
        dict[rec.Id] = owner;
    }
    internal static async Task Retire(OrdinaryRetentionPreparationOwner owner)
    { owner.Dispose(); Assert.True((await owner.Retirement.WaitAsync(TimeSpan.FromSeconds(5))).Retired); }
    public void Dispose() => Engine.Dispose();
}
