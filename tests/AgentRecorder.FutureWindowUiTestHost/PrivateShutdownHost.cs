using System.Net;
using System.Net.Sockets;
using System.Reflection;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;

namespace AgentRecorder.FutureWindowUiTestHost;

// No WinForms initialization, UI, devices or capture. A private process owner for exit integration tests.
internal static class PrivateShutdownHost
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        DataDirResolver.SetOverride(root);
        using var guard = SingleInstanceGuard.TryAcquire(@"Local\AgentRecorder.Task365." + Path.GetFileName(root));
        if (!guard.IsAcquired) return 21;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var readiness = (RuntimeReadiness)Activator.CreateInstance(typeof(RuntimeReadiness),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object?[] { "headless", port, null, @"Local\AgentRecorder.Ready.Task365." + Path.GetFileName(root) }, null)!;
        var audit = new AuditLogger();
        using var engine = new RecordingEngine(audit);
        using var history = new History();
        // Test-only ephemeral listen port; production public port remains unchanged.
        var constructor = typeof(ApiServer).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Any(p => p.Name == "listenPort"));
        var values = constructor.GetParameters().Select(p => p.Name switch
        {
            "engine" => (object)engine, "audit" => audit, "tray" => new Tray(),
            "readiness" => readiness, "listenPort" => port,
            _ => p.HasDefaultValue ? p.DefaultValue : null
        }).ToArray();
        var server = (ApiServer)constructor.Invoke(values);
        using var exit = new ManualResetEventSlim();
        server.ConfigureHostShutdown(() => null, () => exit.Set());
        server.Start();
        readiness.MarkReady();
        bool accepted = exit.Wait(TimeSpan.FromSeconds(30));
        bool drained = HostRecordingShutdown.Stop(server, engine, history, "private_idle_shutdown");
        bool retired = readiness.TryDeleteReadyFile().Success;
        return accepted && drained && retired && history.Closed ? 0 : 22;
    }
    private sealed class History : IDisposable { internal bool Closed; public void Dispose() => Closed = true; }
    private sealed class Tray : ITrayContext
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
