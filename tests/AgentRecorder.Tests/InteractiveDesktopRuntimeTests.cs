using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class InteractiveDesktopRuntimeTests
{
    [Fact]
    public void DirectDesktopRequiresActiveUserSessionStationAndInputDesktop()
    {
        var direct = Observation();
        Assert.True(direct.IsOnInteractiveDesktop);
        Assert.True(direct.CanBrokerToInteractiveDesktop);
        Assert.Equal("interactive", direct.Status);

        var sandboxDesktop = direct with
        {
            ThreadDesktop = "CodexSandboxDesktop",
            FailureCode = "INTERACTIVE_DESKTOP_REQUIRED"
        };
        Assert.False(sandboxDesktop.IsOnInteractiveDesktop);
        Assert.True(sandboxDesktop.CanBrokerToInteractiveDesktop);
        Assert.Equal("broker_required", sandboxDesktop.Status);
    }

    [Fact]
    public void DefaultDesktopNameAloneDoesNotProveUserOrSession()
    {
        var wrongUser = Observation() with
        {
            ProcessUserSid = "S-1-5-18",
            FailureCode = "INTERACTIVE_USER_MISMATCH"
        };
        var wrongSession = Observation() with
        {
            ActiveSessionId = Observation().ProcessSessionId + 1,
            FailureCode = "NO_ACTIVE_INTERACTIVE_SESSION"
        };

        Assert.False(wrongUser.IsOnInteractiveDesktop);
        Assert.False(wrongUser.CanBrokerToInteractiveDesktop);
        Assert.False(wrongSession.IsOnInteractiveDesktop);
        Assert.False(wrongSession.CanBrokerToInteractiveDesktop);
    }

    [Fact]
    public void LockedOrMissingInputDesktopCannotBeBrokered()
    {
        var locked = Observation() with { InputDesktop = "Winlogon", FailureCode = "DESKTOP_LOCKED" };
        var missing = Observation() with { InputDesktop = "", FailureCode = "INPUT_DESKTOP_UNAVAILABLE" };

        Assert.False(locked.IsOnInteractiveDesktop);
        Assert.False(locked.CanBrokerToInteractiveDesktop);
        Assert.Equal("unavailable", locked.Status);
        Assert.False(missing.CanBrokerToInteractiveDesktop);
    }

    [Fact]
    public void ReadySnapshotMustMatchActiveSessionAndOwningProcessUser()
    {
        var process = Process.GetCurrentProcess();
        var observation = Observation();
        var ready = new ReadySnapshot
        {
            Ready = true,
            Pid = process.Id,
            Mode = "tray",
            InteractiveDesktopReady = true,
            SessionId = process.SessionId,
            WindowStation = "WinSta0",
            Desktop = "Default"
        };

        Assert.True(InteractiveDesktopRuntime.IsReadySnapshotForActiveUser(
            ready, observation, _ => observation.ActiveUserSid));
        Assert.False(InteractiveDesktopRuntime.IsReadySnapshotForActiveUser(
            ready, observation, _ => "S-1-5-18"));
        var wrongSession = CopyReady(ready);
        wrongSession.SessionId++;
        Assert.False(InteractiveDesktopRuntime.IsReadySnapshotForActiveUser(
            wrongSession, observation, _ => observation.ActiveUserSid));
        var wrongStation = CopyReady(ready);
        wrongStation.WindowStation = "Service-0x0-3e7$";
        Assert.False(InteractiveDesktopRuntime.IsReadySnapshotForActiveUser(
            wrongStation, observation, _ => observation.ActiveUserSid));
    }

    [Fact]
    public void TcpListenerIdentityIsBoundToTheOwningProcess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(InteractiveDesktopRuntime.IsTcpListenerOwnedByProcess(Environment.ProcessId, port));
        Assert.False(InteractiveDesktopRuntime.IsTcpListenerOwnedByProcess(Environment.ProcessId + 1, port));
    }

    private static InteractiveDesktopObservation Observation()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        const string sid = "S-1-5-21-100-200-300-1001";
        return new(Environment.ProcessId, sessionId, sid, sessionId, sid,
            "WinSta0", "Default", "Default", string.Empty);
    }

    private static ReadySnapshot CopyReady(ReadySnapshot value) => new()
    {
        Ready = value.Ready,
        Pid = value.Pid,
        Port = value.Port,
        Mode = value.Mode,
        InteractiveDesktopReady = value.InteractiveDesktopReady,
        SessionId = value.SessionId,
        WindowStation = value.WindowStation,
        Desktop = value.Desktop
    };
}
