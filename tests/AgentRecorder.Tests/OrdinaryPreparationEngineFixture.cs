using System.Text.Json.Nodes;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

internal sealed class OrdinaryPreparationEngineFixture : IDisposable
{
    internal readonly string Root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task355", "native-" + Guid.NewGuid().ToString("N")));
    internal string Videos => Path.Combine(Root, "Videos");
    internal string Media => Path.Combine(Videos, "原始视频.mp4");
    internal readonly RecordingEngine Engine;
    internal readonly Tray Ui = new();
    internal readonly Backend Capture = new();
    internal readonly History Store = new();
    internal readonly OriginalFileScopeFixture.Native Api = new();
    internal readonly OriginalFileHoldAdmission Admission = new();
    internal PublicationIdentityAnchor Anchor;
    private readonly string? _priorMode = Environment.GetEnvironmentVariable("AGENT_RECORDER_TEST_MODE");
    private readonly string? _priorDir = DataDirResolver.HasOverride ? DataDirResolver.Resolve() : null;
    internal OrdinaryPreparationEngineFixture(bool realFile = false)
    {
        Directory.CreateDirectory(Videos); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", "1"); DataDirResolver.SetOverride(Root);
        SystemQuery.SetDisplayProvider(() => new() { new("display_1", "Synthetic", true, new(0, 0, 1920, 1080), 1.0) });
        Engine = new(new RetentionSharedOutputPathTests.QuietAudit()) { CountdownSteps = 1, CountdownInterval = TimeSpan.FromMinutes(1) };
        Engine.SetTray(Ui); Engine.BackendFactory = _ => (Capture, "fake"); Engine.ConfigureOrdinaryRecordingHistory(Store);
        if (realFile)
        {
            File.WriteAllBytes(Media, Enumerable.Range(0, 12).Select(i => (byte)i).ToArray());
            var original = WindowsReadOnlyFileMetadataProbe.Instance.Observe(Media); Assert.Equal(FileObservationOutcome.Sampled, original.Outcome);
            Anchor = new(Media, 12, original.Before!.Identity!);
        }
        else Anchor = new(Media, 12, new(123, 456, 789));
        Api.Anchor = Anchor;
    }
    internal OrdinaryRetentionPreparationService Service(bool native = false)
        => Engine.CreateRetentionPreparationService(Videos, native ? null : Api, native ? null : Admission);
    internal JsonObject Request(string? directory = null, string? name = null, string policy = "fail") => new()
    {
        ["source"] = new JsonObject { ["type"] = "display", ["display_id"] = "display_1" }, ["video"] = new JsonObject { ["fps"] = 15 },
        ["duration_seconds"] = 60, ["countdown_seconds"] = 1,
        ["output"] = new JsonObject { ["directory"] = directory ?? Videos, ["filename"] = name ?? Path.GetFileName(Media), ["conflict_policy"] = policy }
    };
    internal Recording Create(string? directory = null, string? name = null, string policy = "fail")
    { Engine.CreateRecording(Request(directory, name, policy), "preparation-test", Ui); return Assert.Single(Engine._recs.Values); }
    internal sealed class Tray : ITrayContext
    {
        internal Action<ConfirmationDecision>? Confirm;
        internal int Requests;
        public string HostMode => "tray"; public bool SupportsRegionSelectionUi => true;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) { Requests++; Confirm = c; }
        public void RequestRegionSelection(int t, Action<string, int, int, int, int, string, string> c) => throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p) { } public void SetIdle(RecordingUiPresentation p) { } public void SetAllIdle() { } public void ShowError(string t) { }
    }
    internal sealed class Backend : ICaptureBackend
    {
        internal int Starts;
        public void Start(CaptureConfig c, CaptureAuthorizationProof proof) { Starts++; throw new InvalidOperationException("No capture authorized by these tests"); }
        public OutputMeta Stop() => new() { SizeBytes = 0, OutputFileExists = false }; public void OnNaturalExit(Action<int, OutputMeta> c) { }
        public int ExitCode => 0; public void Dispose() { }
    }
    internal sealed class History : IOrdinaryRecordingHistoryStore
    {
        internal readonly Dictionary<string, OrdinaryRecordingHistorySnapshot> Rows = new();
        internal int Registered;
        public void Register(OrdinaryRecordingHistorySnapshot s) { Registered++; Rows.Add(s.RecordingId, s); }
        public bool TryPublish(OrdinaryRecordingHistorySnapshot s) { Rows[s.RecordingId] = s; return true; }
        public OrdinaryRecordingHistoryPage ReadPage(int l, string? c) => new(Rows.Values.ToArray(), null);
        public OrdinaryRecordingHistorySnapshot? Read(string id) => Rows.GetValueOrDefault(id);
        public void RecoverIncomplete(DateTimeOffset at) { }
    }
    public void Dispose()
    {
        try { Engine.Dispose(); }
        finally
        {
            SystemQuery.SetDisplayProvider(null); Environment.SetEnvironmentVariable("AGENT_RECORDER_TEST_MODE", _priorMode);
            if (_priorDir is null) DataDirResolver.ClearOverride(); else DataDirResolver.SetOverride(_priorDir);
        }
    }
}
