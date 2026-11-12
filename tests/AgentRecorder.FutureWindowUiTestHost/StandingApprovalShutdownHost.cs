using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;

namespace AgentRecorder.FutureWindowUiTestHost;

// Own SQLite + synthetic selection/preview + real production approval adapter.
// The only remote mutation tested is authenticated idle shutdown of this fixture.
internal static class StandingApprovalShutdownHost
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root); DataDirResolver.SetOverride(root);
        using var guard = SingleInstanceGuard.TryAcquire(@"Local\AgentRecorder.Task428R." + Path.GetFileName(root));
        if (!guard.IsAcquired) return 41;
        var store = new SqliteOperationalStore(Path.Combine(root, "fixture.db")); store.Initialize();
        using (var c = store.OpenConnection()) using (var cmd = c.CreateCommand())
        { cmd.CommandText = "UPDATE unattended_safety_state SET unattended_mode_code='enabled';"; cmd.ExecuteNonQuery(); }
        var audit = new AuditLogger();
        using var engine = new RecordingEngine(audit);
        var ui = new Ui(root);
        using var coordinator = new StandingPlanSetupCoordinator(store, audit, ui,
            new StandingLeaseSafetyControlService(store), () => new[] { new StandingLeaseDisplayMetadata("synthetic", "synthetic",
                DisplayIdentityResolutionStatus.Resolved, new(0, 0, 800, 600), 96, 96, 800, 600, AuthorizedDisplayOrientation.Landscape) },
            outputReadinessProviderForTest: new Output());
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var readiness = (RuntimeReadiness)Activator.CreateInstance(typeof(RuntimeReadiness), BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object?[] { "headless", port, null, @"Local\AgentRecorder.Ready.Task428R." + Path.GetFileName(root) }, null)!;
        var constructor = typeof(ApiServer).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Any(p => p.Name == "listenPort"));
        var values = constructor.GetParameters().Select(p => p.Name switch
        {
            "engine" => (object)engine, "audit" => audit, "tray" => new QuietTray(), "readiness" => readiness, "listenPort" => port,
            _ => p.HasDefaultValue ? p.DefaultValue : null,
        }).ToArray();
        var server = (ApiServer)constructor.Invoke(values);
        using var exit = new ManualResetEventSlim();
        server.ConfigureHostShutdown(() => coordinator.HostShutdownBusy ? "interaction_in_flight" : null, () => exit.Set());
        server.Start(); readiness.MarkReady();
        var now = DateTimeOffset.UtcNow;
        var created = coordinator.CreateOrGet(new("task428r-private", now.AddSeconds(3), now.AddSeconds(5), now.AddSeconds(6),
            now.AddSeconds(7), TimeSpan.FromSeconds(1), root, "must-not-exist.mp4"));
        File.WriteAllText(Path.Combine(root, "intent"), created.SetupIntentId ?? "");
        _ = Task.Run(async () => { await coordinator.WaitForIdleAsync(); File.WriteAllText(Path.Combine(root, "idle"), "idle"); });
        try
        {
            if (!exit.Wait(TimeSpan.FromSeconds(30))) return 42;
            coordinator.WaitForIdleAsync().GetAwaiter().GetResult();
            var drained = HostRecordingShutdown.Stop(server, engine, new History(), "task428r_private_exit");
            var retired = readiness.TryDeleteReadyFile().Success;
            return drained && retired && !File.Exists(Path.Combine(root, "must-not-exist.mp4")) ? 0 : 43;
        }
        finally { File.WriteAllText(Path.Combine(root, "release-preview"), "release"); server.Stop(); }
    }

    private sealed class Ui : IStandingPlanSetupUi
    {
        private readonly TrayStandingPlanSetupUi _production;
        internal Ui(string root)
        {
            _production = new(() => new UiTextProvider(UiLanguage.EnUs), new()
            {
                PreviewProvider = new Preview(root), DisableApprovalForTests = true, PreviewBudget = TimeSpan.FromMilliseconds(100),
                AfterVisibleForTests = _ => File.WriteAllText(Path.Combine(root, "visible"), "visible"),
            });
        }
        public bool IsInteractiveDesktopAvailable => true;
        public Task<StandingPlanSetupSelection> SelectFreshRegionAsync(CancellationToken token) => Task.FromResult(new StandingPlanSetupSelection(
            "selected", 10, 20, 64, 48, "synthetic", "virtual_screen"));
        public Task<bool> ShowApprovalAsync(StandingLeaseApprovalDetails details, CancellationToken token) => throw new InvalidOperationException();
        public StandingLeaseApprovalOperation BeginApproval(string id, StandingLeaseApprovalDetails details, Action<string, object> audit, CancellationToken token)
            => _production.BeginApproval(id, details, audit, token);
    }
    private sealed class Preview(string root) : IScreenPreviewProvider
    {
        public Bitmap Capture(ConfirmationCaptureBounds bounds, Size maxSize)
        {
            while (!File.Exists(Path.Combine(root, "release-preview"))) Thread.Sleep(20);
            return new Bitmap(32, 16);
        }
    }
    private sealed class Output : IStandingLeaseOutputReadinessProvider
    {
        public StandingLeaseOutputReadinessResult Check(string directory, string file, TimeSpan duration) => StandingLeaseOutputReadinessResult.Ready(
            new(directory, Path.Combine(directory, file), true, false, true, true, long.MaxValue, 1));
    }
    private sealed class History : IDisposable { public void Dispose() { } }
    private sealed class QuietTray : ITrayContext
    {
        public string HostMode => "headless";
        public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> c) => throw new InvalidOperationException();
        public void RequestRegionSelection(int s, Action<string, int, int, int, int, string, string> c) => throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p) { }
        public void SetIdle(RecordingUiPresentation p) { }
        public void SetAllIdle() { }
        public void ShowError(string s) { }
    }
}
