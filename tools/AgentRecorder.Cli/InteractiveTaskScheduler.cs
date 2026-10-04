using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Cli;

internal sealed record InteractiveTaskDefinition(
    string TaskName,
    string UserSid,
    string AppPath,
    string Arguments,
    string WorkingDirectory,
    string Description,
    int LogonType = 3,
    int RunLevel = 0,
    int MultipleInstances = 2,
    int TriggerCount = 0,
    bool Enabled = true,
    bool AllowDemandStart = true,
    string ExecutionTimeLimit = "PT0S");

internal sealed record InteractiveTaskReadback(
    InteractiveTaskDefinition Definition,
    bool Exists,
    string FailureCode = "");

internal interface IInteractiveTaskRun : IDisposable
{
    int ProcessId { get; }
    void Stop();
}

internal interface IInteractiveTaskScheduler
{
    InteractiveTaskReadback Read(string taskName);
    bool TryCreate(InteractiveTaskDefinition definition, out string failureCode);
    bool TryRun(string taskName, int sessionId, string requestId, out IInteractiveTaskRun? run, out string failureCode);
    bool TryDelete(string taskName, out string failureCode);
}

internal static class InteractiveTaskDefinitions
{
    internal static InteractiveTaskDefinition For(InteractiveLaunchRegistration registration) => new(
        registration.TaskName,
        registration.UserSid,
        registration.AppPath,
        InteractiveLaunchProtocol.CreateTaskArguments(registration.RegistrationId),
        System.IO.Path.GetDirectoryName(registration.AppPath)!,
        CreateDescription(registration),
        MultipleInstances: 2);

    internal static string CreateDescription(InteractiveLaunchRegistration registration)
    {
        var dataHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(registration.DataDir)))
            .ToLowerInvariant();
        return $"AgentRecorder interactive launch v1; registration={registration.RegistrationId}; " +
               $"user={registration.UserSid}; app={registration.AppSha256}; data={dataHash}";
    }

    internal static bool Matches(InteractiveTaskReadback readback, InteractiveTaskDefinition expected)
    {
        try
        {
            if (!readback.Exists || readback.Definition is null)
                return false;
            var actual = readback.Definition;
            return string.Equals(actual.TaskName, expected.TaskName, StringComparison.Ordinal) &&
                   string.Equals(NormalizeSid(actual.UserSid), expected.UserSid, StringComparison.Ordinal) &&
                   PathsEqual(actual.AppPath, expected.AppPath) &&
                   string.Equals(actual.Arguments, expected.Arguments, StringComparison.Ordinal) &&
                   PathsEqual(actual.WorkingDirectory, expected.WorkingDirectory) &&
                   string.Equals(actual.Description, expected.Description, StringComparison.Ordinal) &&
                   actual.LogonType == expected.LogonType && actual.RunLevel == expected.RunLevel &&
                   actual.MultipleInstances == expected.MultipleInstances && actual.TriggerCount == 0 &&
                   actual.Enabled && actual.AllowDemandStart &&
                   string.Equals(actual.ExecutionTimeLimit, expected.ExecutionTimeLimit, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string NormalizeSid(string value)
    {
        try
        {
            if (value.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
                return new SecurityIdentifier(value).Value;
            return (new NTAccount(value).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value ?? "";
        }
        catch { return ""; }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(left), System.IO.Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>
/// Narrow Task Scheduler COM adapter. It creates only an interactive-token
/// task for the current user and fixed AgentRecorder.App action.
/// </summary>
internal sealed class WindowsInteractiveTaskScheduler : IInteractiveTaskScheduler
{
    private const int TaskCreate = 2;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunUseSessionId = 4;
    private const int TaskRunLevelLua = 0;
    private const int TaskExecAction = 0;
    private const int TaskNotFound = unchecked((int)0x8004130F);

    public InteractiveTaskReadback Read(string taskName)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        object? definition = null;
        object? principal = null;
        object? actions = null;
        object? action = null;
        object? triggers = null;
        object? settings = null;
        try
        {
            dynamic s = CreateService(); service = s;
            s.Connect();
            dynamic f = s.GetFolder("\\"); folder = f;
            dynamic t;
            try { t = f.GetTask("\\" + taskName); task = t; }
            catch (COMException ex) when (ex.HResult == TaskNotFound || ex.HResult == unchecked((int)0x80070002))
            {
                return new(new(taskName, "", "", "", "", ""), false, "TASK_NOT_FOUND");
            }

            dynamic d = t.Definition; definition = d;
            dynamic p = d.Principal; principal = p;
            dynamic a = d.Actions; actions = a;
            dynamic g = d.Triggers; triggers = g;
            dynamic cfg = d.Settings; settings = cfg;
            var count = Convert.ToInt32(a.Count);
            if (count != 1)
                return new(new(taskName, "", "", "", "", "", TriggerCount: Convert.ToInt32(g.Count)), true);
            dynamic item = a.Item(1); action = item;
            if (Convert.ToInt32(item.Type) != TaskExecAction)
                return new(new(taskName, "", "", "", "", "", TriggerCount: Convert.ToInt32(g.Count)), true);

            var snapshot = new InteractiveTaskDefinition(
                taskName,
                Convert.ToString(p.UserId) ?? "",
                Convert.ToString(item.Path) ?? "",
                Convert.ToString(item.Arguments) ?? "",
                Convert.ToString(item.WorkingDirectory) ?? "",
                Convert.ToString(d.RegistrationInfo.Description) ?? "",
                Convert.ToInt32(p.LogonType),
                Convert.ToInt32(p.RunLevel),
                Convert.ToInt32(cfg.MultipleInstances),
                Convert.ToInt32(g.Count),
                Convert.ToBoolean(t.Enabled),
                Convert.ToBoolean(cfg.AllowDemandStart),
                ExecutionTimeLimit: Convert.ToString(cfg.ExecutionTimeLimit) ?? "");
            return new(snapshot, true);
        }
        catch
        {
            return new(new(taskName, "", "", "", "", ""), false, "TASK_SCHEDULER_UNAVAILABLE");
        }
        finally
        {
            ReleaseCom(action); ReleaseCom(settings); ReleaseCom(triggers); ReleaseCom(actions);
            ReleaseCom(principal); ReleaseCom(definition); ReleaseCom(task); ReleaseCom(folder); ReleaseCom(service);
        }
    }

    public bool TryCreate(InteractiveTaskDefinition definition, out string failureCode)
    {
        failureCode = "TASK_REGISTRATION_FAILED";
        object? service = null;
        object? folder = null;
        object? taskDefinition = null;
        object? principal = null;
        object? settings = null;
        object? actions = null;
        object? action = null;
        try
        {
            dynamic s = CreateService(); service = s;
            s.Connect();
            dynamic f = s.GetFolder("\\"); folder = f;
            dynamic d = s.NewTask(0); taskDefinition = d;
            d.RegistrationInfo.Description = definition.Description;
            d.RegistrationInfo.Author = "Agent Recorder";
            dynamic p = d.Principal; principal = p;
            var accountName = new SecurityIdentifier(definition.UserSid)
                .Translate(typeof(NTAccount)).Value;
            p.UserId = accountName;
            p.LogonType = TaskLogonInteractiveToken;
            p.RunLevel = TaskRunLevelLua;
            dynamic cfg = d.Settings; settings = cfg;
            cfg.Enabled = true;
            cfg.AllowDemandStart = true;
            cfg.Hidden = true;
            cfg.StartWhenAvailable = false;
            cfg.MultipleInstances = definition.MultipleInstances;
            cfg.DisallowStartIfOnBatteries = false;
            cfg.StopIfGoingOnBatteries = false;
            cfg.ExecutionTimeLimit = definition.ExecutionTimeLimit;
            dynamic acts = d.Actions; actions = acts;
            dynamic exec = acts.Create(TaskExecAction); action = exec;
            exec.Path = definition.AppPath;
            exec.Arguments = definition.Arguments;
            exec.WorkingDirectory = definition.WorkingDirectory;

            // TASK_CREATE refuses to overwrite an existing or mismatched task.
            _ = f.RegisterTaskDefinition(definition.TaskName, d, TaskCreate,
                accountName, null, TaskLogonInteractiveToken, null);
            failureCode = string.Empty;
            return true;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x800700B7))
        {
            failureCode = "TASK_NAME_CONFLICT";
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseCom(action); ReleaseCom(actions); ReleaseCom(settings); ReleaseCom(principal);
            ReleaseCom(taskDefinition); ReleaseCom(folder); ReleaseCom(service);
        }
    }

    public bool TryRun(
        string taskName,
        int sessionId,
        string requestId,
        out IInteractiveTaskRun? run,
        out string failureCode)
    {
        run = null;
        failureCode = "TASK_START_FAILED";
        if (sessionId <= 0 || !InteractiveLaunchProtocol.IsId(requestId))
        {
            failureCode = "INTERACTIVE_SESSION_UNAVAILABLE";
            return false;
        }

        object? service = null;
        object? folder = null;
        object? task = null;
        object? running = null;
        try
        {
            dynamic s = CreateService(); service = s;
            s.Connect();
            dynamic f = s.GetFolder("\\"); folder = f;
            dynamic t = f.GetTask("\\" + taskName); task = t;
            dynamic r = t.RunEx(new object[] { requestId }, TaskRunUseSessionId, sessionId, null); running = r;
            run = new WindowsInteractiveTaskRun(running, Convert.ToInt32(r.EnginePID));
            running = null;
            failureCode = string.Empty;
            return true;
        }
        catch (COMException ex) when (ex.HResult == TaskNotFound || ex.HResult == unchecked((int)0x80070002))
        {
            failureCode = "TASK_NOT_FOUND";
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseCom(running); ReleaseCom(task); ReleaseCom(folder); ReleaseCom(service);
        }
    }

    public bool TryDelete(string taskName, out string failureCode)
    {
        failureCode = "TASK_DELETE_FAILED";
        object? service = null;
        object? folder = null;
        try
        {
            dynamic s = CreateService(); service = s;
            s.Connect();
            dynamic f = s.GetFolder("\\"); folder = f;
            f.DeleteTask(taskName, 0);
            failureCode = string.Empty;
            return true;
        }
        catch (COMException ex) when (ex.HResult == TaskNotFound || ex.HResult == unchecked((int)0x80070002))
        {
            failureCode = "TASK_NOT_FOUND";
            return false;
        }
        catch { return false; }
        finally { ReleaseCom(folder); ReleaseCom(service); }
    }

    private static object CreateService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");
        return Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Task Scheduler COM service could not be created.");
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }

    private sealed class WindowsInteractiveTaskRun(object runningTask, int processId) : IInteractiveTaskRun
    {
        private object? _runningTask = runningTask;
        public int ProcessId { get; } = processId;
        public void Stop()
        {
            if (_runningTask is null) return;
            try { ((dynamic)_runningTask).Stop(0); } catch { }
        }
        public void Dispose()
        {
            ReleaseCom(_runningTask);
            _runningTask = null;
        }
    }
}
