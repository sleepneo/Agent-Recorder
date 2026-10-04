using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using AgentRecorder.Cli;
using AgentRecorder.Infrastructure;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class InteractiveLaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-recorder-interactive-launch-" + Guid.NewGuid().ToString("N"));
    private readonly string _appPath;
    private readonly string _dataDir;
    private readonly string _registrationPath;
    private readonly string _userSid;
    private readonly InteractiveDesktopObservation _desktop;
    private readonly FakeTaskScheduler _tasks = new();

    public InteractiveLaunchTests()
    {
        Directory.CreateDirectory(_root);
        _appPath = Path.Combine(_root, "AgentRecorder.App.exe");
        File.WriteAllBytes(_appPath, new byte[] { 1, 2, 3, 4, 5 });
        _dataDir = Path.Combine(_root, "data");
        _registrationPath = Path.Combine(_root, "interactive-launch.json");
        _userSid = WindowsIdentity.GetCurrent().User!.Value;
        var session = Process.GetCurrentProcess().SessionId;
        _desktop = new(Environment.ProcessId, session, _userSid, session, _userSid,
            "WinSta0", "Default", "Default", string.Empty);
    }

    [Fact]
    public void DirectInteractiveStartDoesNotRequireTaskEnrollment()
    {
        var result = InteractiveLaunchDispatcher.Dispatch(
            _desktop, _appPath, _dataDir, InteractiveLaunchProtocol.NewId(), _tasks, _registrationPath);

        Assert.True(result.Started);
        Assert.Equal("direct", result.LaunchPath);
        Assert.Null(result.TaskRun);
        Assert.Equal(0, _tasks.RunCount);
        Assert.False(File.Exists(_registrationPath));
    }

    [Fact]
    public void BrokerDispatchUsesOnlyRegisteredActionAndExactActiveSession()
    {
        var setup = Manager().Setup(_appPath, _dataDir);
        Assert.True(setup.Ok);
        var requestId = InteractiveLaunchProtocol.NewId();
        var brokerDesktop = _desktop with
        {
            ThreadDesktop = "CodexSandboxDesktop",
            FailureCode = "INTERACTIVE_DESKTOP_REQUIRED"
        };

        var result = InteractiveLaunchDispatcher.Dispatch(
            brokerDesktop, _appPath, _dataDir, requestId, _tasks, _registrationPath);

        Assert.True(result.Started);
        Assert.Equal("task_scheduler_interactive_token", result.LaunchPath);
        Assert.Equal(_desktop.ActiveSessionId, _tasks.LastSessionId);
        Assert.Equal(requestId, _tasks.LastRequestId);
        Assert.Equal(1, _tasks.RunCount);
        result.TaskRun?.Dispose();
    }

    [Fact]
    public void EnrollmentIsOneTimeAndReversibleWithoutTouchingRealScheduler()
    {
        var manager = Manager();
        var setup = manager.Setup(_appPath, _dataDir);
        Assert.True(setup.Ok);
        Assert.Single(_tasks.Definitions);

        var repeated = manager.Setup(_appPath, _dataDir);
        Assert.True(repeated.Ok);
        Assert.Equal("configured", repeated.Status);
        Assert.Single(_tasks.Definitions);
        Assert.True(manager.Status(_appPath, _dataDir).Ok);

        var removed = manager.Remove();
        Assert.True(removed.Ok);
        Assert.Empty(_tasks.Definitions);
        Assert.False(File.Exists(_registrationPath));
    }

    [Fact]
    public void EnrollmentRequiresVisibleDesktopAndSameUser()
    {
        var manager = Manager(_desktop with { ThreadDesktop = "CodexSandboxDesktop", FailureCode = "INTERACTIVE_DESKTOP_REQUIRED" });
        var result = manager.Setup(_appPath, _dataDir);
        Assert.False(result.Ok);
        Assert.Equal("INTERACTIVE_DESKTOP_REQUIRED", result.Code);
        Assert.Empty(_tasks.Definitions);
        Assert.False(File.Exists(_registrationPath));

        var mismatch = Manager(_desktop, "S-1-5-18").Setup(_appPath, _dataDir);
        Assert.False(mismatch.Ok);
        Assert.Equal("INTERACTIVE_USER_MISMATCH", mismatch.Code);
    }

    [Fact]
    public void StaleBinaryAndDifferentDataDirectoryAreRejected()
    {
        Assert.True(Manager().Setup(_appPath, _dataDir).Ok);
        File.WriteAllBytes(_appPath, new byte[] { 9, 8, 7, 6 });

        Assert.Equal("INTERACTIVE_LAUNCH_STALE_BINARY", Manager().Status(_appPath, _dataDir).Code);

        File.WriteAllBytes(_appPath, new byte[] { 1, 2, 3, 4, 5 });
        var otherDataDir = Path.Combine(_root, "other-data");
        var brokerDesktop = _desktop with { ThreadDesktop = "AgentSandbox", FailureCode = "INTERACTIVE_DESKTOP_REQUIRED" };
        var dispatch = InteractiveLaunchDispatcher.Dispatch(
            brokerDesktop, _appPath, otherDataDir, InteractiveLaunchProtocol.NewId(), _tasks, _registrationPath);
        Assert.False(dispatch.Started);
        Assert.Equal("INTERACTIVE_LAUNCH_DATA_DIR_MISMATCH", dispatch.FailureCode);
        Assert.Equal(0, _tasks.RunCount);
    }

    [Fact]
    public void WrongUserLockedDesktopAndTaskTamperingFailClosed()
    {
        Assert.True(Manager().Setup(_appPath, _dataDir).Ok);
        var requestId = InteractiveLaunchProtocol.NewId();

        var wrongUser = _desktop with { ProcessUserSid = "S-1-5-18", FailureCode = "INTERACTIVE_USER_MISMATCH" };
        var wrongUserResult = InteractiveLaunchDispatcher.Dispatch(wrongUser, _appPath, _dataDir,
            requestId, _tasks, _registrationPath);
        Assert.False(wrongUserResult.Started);
        Assert.Equal("INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED", wrongUserResult.FailureCode);

        var locked = _desktop with { InputDesktop = "Winlogon", FailureCode = "DESKTOP_LOCKED" };
        var lockedResult = InteractiveLaunchDispatcher.Dispatch(locked, _appPath, _dataDir,
            requestId, _tasks, _registrationPath);
        Assert.False(lockedResult.Started);
        Assert.Equal("DESKTOP_LOCKED", lockedResult.FailureCode);

        _tasks.Mutate(_tasks.Definitions.Keys.Single(), d => d with { Arguments = "powershell.exe" });
        var tampered = InteractiveLaunchDispatcher.Dispatch(_desktop with
            { ThreadDesktop = "AgentSandbox", FailureCode = "INTERACTIVE_DESKTOP_REQUIRED" },
            _appPath, _dataDir, requestId, _tasks, _registrationPath);
        Assert.False(tampered.Started);
        Assert.Equal("TASK_DEFINITION_MISMATCH", tampered.FailureCode);
        Assert.Equal(0, _tasks.RunCount);
        Assert.Equal("TASK_DEFINITION_MISMATCH", Manager().Remove().Code);
        Assert.Single(_tasks.Definitions);
    }

    [Fact]
    public void FailedTaskEnrollmentRemovesItsRegistrationFile()
    {
        _tasks.FailCreate = true;
        var result = Manager().Setup(_appPath, _dataDir);
        Assert.False(result.Ok);
        Assert.Equal("TASK_REGISTRATION_FAILED", result.Code);
        Assert.False(File.Exists(_registrationPath));
        Assert.Empty(_tasks.Definitions);
    }

    [Fact]
    public void CrossAccountDispatchIsUnsupportedWithoutSchedulerOrFilesystemSideEffects()
    {
        var otherSid = "S-1-5-21-101-202-303-1002";
        var isolated = _desktop with
        {
            ProcessUserSid = otherSid,
            ThreadDesktop = "CodexSandboxDesktop",
            FailureCode = "INTERACTIVE_USER_MISMATCH"
        };

        var result = InteractiveLaunchDispatcher.Dispatch(
            isolated, _appPath, _dataDir, InteractiveLaunchProtocol.NewId(), _tasks, _registrationPath);

        Assert.False(result.Started);
        Assert.Equal("INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED", result.FailureCode);
        Assert.Equal("none", result.LaunchPath);
        Assert.Equal(0, _tasks.ReadCount);
        Assert.Equal(0, _tasks.RunCount);
        Assert.False(File.Exists(_registrationPath));
        Assert.False(Directory.Exists(_dataDir));
    }

    [Fact]
    public void LegacyCrossAccountRegistrationIsExplicitlyUnsupportedAndNeverMutated()
    {
        const string agentSid = "S-1-5-21-101-202-303-1002";
        var legacy = new
        {
            schema_version = 2,
            registration_id = Guid.NewGuid().ToString("N"),
            user_sid = _userSid,
            app_path = _appPath,
            app_sha256 = new string('a', 64),
            data_dir = _dataDir,
            task_name = "AgentRecorder.InteractiveLaunch.v2.legacy",
            enrolled_at_utc = DateTimeOffset.UtcNow,
            agent_sid = agentSid
        };
        File.WriteAllText(_registrationPath, System.Text.Json.JsonSerializer.Serialize(legacy));
        var manager = Manager();

        Assert.Equal("INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED", manager.Status(_appPath, _dataDir).Code);
        Assert.Equal("INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED", manager.Remove().Code);
        Assert.Equal("INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED", manager.Setup(_appPath, _dataDir).Code);
        Assert.Equal("INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED",
            manager.Setup(Path.Combine(_root, "missing-app.exe"), _dataDir).Code);
        Assert.True(File.Exists(_registrationPath));
        Assert.Equal(0, _tasks.ReadCount);
        Assert.Equal(0, _tasks.DeleteCount);
        Assert.Empty(_tasks.Definitions);
    }

    [Fact]
    public void LegacyAgentSidOptionIsAlwaysRejectedBeforeInteractiveLaunchSetup()
    {
        Assert.Equal("INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED",
            Program.GetUnsupportedCrossAccountOptionCode(new[]
            {
                "interactive-launch", "setup", "--json", "--agent-sid", "S-1-5-21-101-202-303-1002"
            }));
        Assert.Equal("INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED",
            Program.GetUnsupportedCrossAccountOptionCode(new[]
            {
                "interactive-launch", "setup", "--json", "--agent-sid=S-1-5-21-101-202-303-1002"
            }));
        Assert.Null(Program.GetUnsupportedCrossAccountOptionCode(new[]
        {
            "interactive-launch", "setup", "--json", "--app", "AgentRecorder.App.exe"
        }));
    }

    [Fact]
    public void FailedBrokerStartupStopsTheLaunchedTaskInstance()
    {
        Assert.True(Manager().Setup(_appPath, _dataDir).Ok);
        var brokerDesktop = _desktop with
        {
            ThreadDesktop = "CodexSandboxDesktop",
            FailureCode = "INTERACTIVE_DESKTOP_REQUIRED"
        };
        var dispatch = InteractiveLaunchDispatcher.Dispatch(
            brokerDesktop, _appPath, _dataDir, InteractiveLaunchProtocol.NewId(), _tasks, _registrationPath);

        Assert.True(dispatch.Started);
        Assert.Equal(0, _tasks.LastStopCount);

        AgentRecorder.Cli.Program.StopFailedStartup(null, dispatch.TaskRun);

        Assert.Equal(1, _tasks.LastStopCount);
        dispatch.TaskRun?.Dispose();
    }

    [Fact]
    public void LaunchResultIsBoundedAndConsumedOnce()
    {
        var requestId = InteractiveLaunchProtocol.NewId();
        Assert.True(InteractiveLaunchResultStore.TryWrite(_dataDir, requestId, "rejected", "DESKTOP_LOCKED"));
        Assert.True(InteractiveLaunchResultStore.TryReadAndDelete(_dataDir, requestId, out var result));
        Assert.Equal("rejected", result!.Status);
        Assert.Equal("DESKTOP_LOCKED", result.FailureCode);
        Assert.False(InteractiveLaunchResultStore.TryReadAndDelete(_dataDir, requestId, out _));
        Assert.False(InteractiveLaunchResultStore.TryReadAndDelete(_dataDir, "../bad", out _));
    }

    [Fact]
    public void AbandonedLaunchResultsAreCountBounded()
    {
        for (var i = 0; i < 40; i++)
            Assert.True(InteractiveLaunchResultStore.TryWrite(_dataDir,
                Guid.NewGuid().ToString("N"), "desktop_ready"));
        Assert.InRange(Directory.GetFiles(Path.Combine(_dataDir, "runtime", "interactive-launch"), "*.json").Length,
            1, 32);
    }

    [Fact]
    public void AppLaunchProtocolRejectsArbitraryArguments()
    {
        Assert.True(InteractiveLaunchProtocol.TryParseAppArguments(Array.Empty<string>(), out var noArgs, out _));
        Assert.Null(noArgs);
        Assert.True(InteractiveLaunchProtocol.TryParseAppArguments(
            new[] { InteractiveLaunchProtocol.DirectStartArgument, InteractiveLaunchProtocol.NewId() }, out var direct, out _));
        Assert.False(direct!.IsBrokered);
        Assert.False(InteractiveLaunchProtocol.TryParseAppArguments(
            new[] { "--execute", "powershell.exe" }, out _, out var code));
        Assert.Equal("UNTRUSTED_APP_ARGUMENTS", code);
    }

    private InteractiveLaunchManager Manager(InteractiveDesktopObservation? desktop = null, string? userSid = null) =>
        new(_tasks, _registrationPath, () => desktop ?? _desktop, () => userSid ?? _userSid,
            isElevated: () => false);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeTaskScheduler : IInteractiveTaskScheduler
    {
        internal Dictionary<string, InteractiveTaskDefinition> Definitions { get; } = new(StringComparer.Ordinal);
        private int _runCount;
        internal int RunCount => System.Threading.Volatile.Read(ref _runCount);
        private int _readCount;
        internal int ReadCount => System.Threading.Volatile.Read(ref _readCount);
        private int _deleteCount;
        internal int DeleteCount => System.Threading.Volatile.Read(ref _deleteCount);
        internal int LastSessionId { get; private set; }
        internal string? LastRequestId { get; private set; }
        internal bool FailCreate { get; set; }
        internal bool FailRun { get; set; }
        private FakeTaskRun? _lastRun;
        internal int LastStopCount => _lastRun?.StopCount ?? 0;

        public InteractiveTaskReadback Read(string taskName)
        {
            System.Threading.Interlocked.Increment(ref _readCount);
            return Definitions.TryGetValue(taskName, out var definition)
                ? new(definition, true)
                : new(new(taskName, "", "", "", "", ""), false, "TASK_NOT_FOUND");
        }

        public bool TryCreate(InteractiveTaskDefinition definition, out string failureCode)
        {
            if (FailCreate || Definitions.ContainsKey(definition.TaskName))
            {
                failureCode = "TASK_REGISTRATION_FAILED";
                return false;
            }
            Definitions.Add(definition.TaskName, definition);
            failureCode = string.Empty;
            return true;
        }

        public bool TryRun(string taskName, int sessionId, string requestId, out IInteractiveTaskRun? run, out string failureCode)
        {
            run = null;
            if (FailRun)
            {
                failureCode = "TASK_START_FAILED";
                return false;
            }
            if (!Definitions.ContainsKey(taskName))
            {
                failureCode = "TASK_NOT_FOUND";
                return false;
            }
            System.Threading.Interlocked.Increment(ref _runCount);
            LastSessionId = sessionId;
            LastRequestId = requestId;
            _lastRun = new FakeTaskRun();
            run = _lastRun;
            failureCode = string.Empty;
            return true;
        }

        public bool TryDelete(string taskName, out string failureCode)
        {
            System.Threading.Interlocked.Increment(ref _deleteCount);
            if (Definitions.Remove(taskName))
            {
                failureCode = string.Empty;
                return true;
            }
            failureCode = "TASK_NOT_FOUND";
            return false;
        }

        internal void Mutate(string taskName, Func<InteractiveTaskDefinition, InteractiveTaskDefinition> mutation) =>
            Definitions[taskName] = mutation(Definitions[taskName]);

        private sealed class FakeTaskRun : IInteractiveTaskRun
        {
            public int ProcessId => 42;
            public int StopCount { get; private set; }
            public void Stop() => StopCount++;
            public void Dispose() { }
        }
    }
}
