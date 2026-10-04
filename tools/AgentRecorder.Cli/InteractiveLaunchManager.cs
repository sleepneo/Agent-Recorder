using System;
using System.IO;
using System.Security.Principal;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Cli;

internal sealed record InteractiveLaunchOperationResult(
    bool Ok,
    string Status,
    string? Code = null,
    string? Message = null,
    string? SuggestedAction = null,
    string? TaskName = null);

internal sealed record InteractiveLaunchDiagnostic(
    int ProcessId,
    string UserSid,
    int SessionId,
    int ActiveSessionId,
    string WindowStation,
    string Desktop,
    string InputDesktop,
    string LaunchPathResult,
    string FailureCode);

internal sealed record InteractiveLaunchDispatchResult(
    bool Started,
    string LaunchPath,
    string FailureCode,
    IInteractiveTaskRun? TaskRun = null);

internal static class InteractiveLaunchDispatcher
{
    internal static InteractiveLaunchDispatchResult Dispatch(
        InteractiveDesktopObservation desktop,
        string appPath,
        string dataDir,
        string requestId,
        IInteractiveTaskScheduler tasks,
        string? registrationPath = null)
    {
        if (desktop.IsOnInteractiveDesktop)
            return new(true, "direct", string.Empty);

        if (!string.Equals(desktop.ProcessUserSid, desktop.ActiveUserSid, StringComparison.Ordinal))
            return new(false, "none", "INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED");

        if (!desktop.CanBrokerToInteractiveDesktop)
            return new(false, "none", desktop.FailureCode.Length == 0
                ? "INTERACTIVE_DESKTOP_REQUIRED" : desktop.FailureCode);

        var loaded = registrationPath is null
            ? InteractiveLaunchRegistrationStore.TryLoad(out var registration, out var loadFailure)
            : InteractiveLaunchRegistrationStore.TryLoad(registrationPath, out registration, out loadFailure);
        if (!loaded || registration is null)
        {
            var code = loadFailure == "INTERACTIVE_LAUNCH_REGISTRATION_MISSING"
                ? "INTERACTIVE_LAUNCH_NOT_ENROLLED" : loadFailure;
            return new(false, "none", code);
        }

        var validation = InteractiveLaunchRegistrationStore.Validate(
            registration, registration.RegistrationId, desktop.ProcessUserSid, appPath, dataDir);
        if (validation.Length != 0)
            return new(false, "none", validation);

        var expected = InteractiveTaskDefinitions.For(registration);
        var readback = tasks.Read(registration.TaskName);
        if (!InteractiveTaskDefinitions.Matches(readback, expected))
        {
            var code = readback.Exists ? "TASK_DEFINITION_MISMATCH" :
                readback.FailureCode == "TASK_NOT_FOUND" ? "TASK_NOT_FOUND" : "TASK_SCHEDULER_UNAVAILABLE";
            return new(false, "none", code);
        }

        if (!tasks.TryRun(registration.TaskName, desktop.ActiveSessionId, requestId,
                out var taskRun, out var runFailure))
            return new(false, "none", runFailure);
        return new(true, "task_scheduler_interactive_token", string.Empty, taskRun);
    }
}

internal sealed class InteractiveLaunchManager
{
    private readonly IInteractiveTaskScheduler _tasks;
    private readonly string _registrationPath;
    private readonly Func<InteractiveDesktopObservation> _observe;
    private readonly Func<string> _currentUserSid;
    private readonly Func<string, bool>? _userSidValidator;
    private readonly Func<bool> _isElevated;

    internal InteractiveLaunchManager(
        IInteractiveTaskScheduler tasks,
        string? registrationPath = null,
        Func<InteractiveDesktopObservation>? observe = null,
        Func<string>? currentUserSid = null,
        Func<string, bool>? userSidValidator = null,
        Func<bool>? isElevated = null)
    {
        _tasks = tasks;
        _registrationPath = registrationPath ?? InteractiveLaunchRegistrationStore.RegistrationPath;
        _observe = observe ?? InteractiveDesktopRuntime.ObserveCurrent;
        _currentUserSid = currentUserSid ?? (() => WindowsIdentity.GetCurrent().User?.Value ?? string.Empty);
        _userSidValidator = userSidValidator;
        _isElevated = isElevated ?? IsCurrentProcessElevated;
    }

    internal InteractiveLaunchOperationResult Setup(string appPath, string dataDir)
    {
        var desktop = _observe();
        if (!desktop.IsOnInteractiveDesktop)
            return Error("INTERACTIVE_DESKTOP_REQUIRED",
                "Interactive launch setup must be approved once from the user's unlocked desktop.",
                "Run 'AgentRecorder.Cli.exe interactive-launch setup --json' once from a normal, non-elevated user desktop.");
        if (_isElevated())
            return Error("INTERACTIVE_LAUNCH_ELEVATION_NOT_ALLOWED",
                "Enrollment must run without an elevated token.",
                "Start a normal, non-elevated shell in the target user's unlocked desktop and retry.");

        var userSid = _currentUserSid();
        if (string.IsNullOrWhiteSpace(userSid) || !string.Equals(userSid, desktop.ActiveUserSid, StringComparison.Ordinal))
            return Error("INTERACTIVE_USER_MISMATCH", "The current process does not match the active desktop user.");

        if (!InteractiveLaunchRegistrationStore.TryLoad(_registrationPath, out _, out var existingRegistrationFailure) &&
            existingRegistrationFailure == "INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED")
            return Error(existingRegistrationFailure,
                "This legacy cross-account registration is unsupported; no task or registration was changed.",
                "Review and revoke any legacy cross-account task from the target user's desktop.");

        if (!InteractiveLaunchRegistrationStore.TryCreate(appPath, dataDir, userSid,
                out var proposed, out var createFailure, _userSidValidator) || proposed is null)
            return Error(createFailure, "The App path or data directory cannot be safely bound to an interactive launch.",
                "Use a local AgentRecorder.App.exe path and the intended per-user data directory.");

        if (InteractiveLaunchRegistrationStore.TryLoad(_registrationPath, out var existing, out var loadFailure))
        {
            if (existing is null)
                return Error("INTERACTIVE_LAUNCH_REGISTRATION_INVALID", "The existing registration is invalid.");
            var validation = InteractiveLaunchRegistrationStore.Validate(
                existing, existing.RegistrationId, userSid, proposed.AppPath, proposed.DataDir);
            if (validation.Length != 0)
                return Error(validation, "A different or stale interactive launch registration already exists.",
                    "Run 'AgentRecorder.Cli.exe interactive-launch remove --json' from the same user, then enroll the intended App/data directory.");
            var readback = _tasks.Read(existing.TaskName);
            if (readback.Exists && !InteractiveTaskDefinitions.Matches(readback, InteractiveTaskDefinitions.For(existing)))
                return Error("TASK_DEFINITION_MISMATCH", "The registered task no longer matches its fixed Agent Recorder definition.");
            if (!readback.Exists)
            {
                if (readback.FailureCode != "TASK_NOT_FOUND")
                    return Error("TASK_SCHEDULER_UNAVAILABLE", "Task Scheduler could not be read safely; the existing registration was preserved.");
                var expected = InteractiveTaskDefinitions.For(existing);
                if (!_tasks.TryCreate(expected, out var repairFailure))
                    return Error(repairFailure, "The missing per-user interactive task could not be restored.");
                readback = _tasks.Read(existing.TaskName);
                if (!InteractiveTaskDefinitions.Matches(readback, expected))
                    return Error("TASK_DEFINITION_MISMATCH", "Task Scheduler readback did not match the repaired definition.");
            }
            return new(true, "configured", TaskName: existing.TaskName);
        }
        else if (loadFailure != "INTERACTIVE_LAUNCH_REGISTRATION_MISSING")
        {
            return Error(loadFailure,
                loadFailure == "INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED"
                    ? "This legacy cross-account registration is unsupported; no task or registration was changed."
                    : "The existing interactive launch registration could not be read safely.",
                loadFailure == "INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED"
                    ? "Review and revoke any legacy cross-account task from the target user's desktop."
                    : null);
        }

        var definition = InteractiveTaskDefinitions.For(proposed);
        var priorTask = _tasks.Read(proposed.TaskName);
        if (priorTask.Exists)
            return Error("TASK_NAME_CONFLICT", "A task already exists at the per-user Agent Recorder task name; it was not changed.");

        if (!InteractiveLaunchRegistrationStore.TrySaveNew(proposed, _registrationPath, out var saveFailure))
            return Error(saveFailure, "The per-user registration file could not be written.");

        if (!_tasks.TryCreate(definition, out var taskFailure))
        {
            var afterFailure = _tasks.Read(proposed.TaskName);
            if (InteractiveTaskDefinitions.Matches(afterFailure, definition))
                return new(true, "configured", TaskName: proposed.TaskName);
            _ = InteractiveLaunchRegistrationStore.TryDelete(proposed.RegistrationId, _registrationPath, out _);
            return Error(taskFailure, "Task Scheduler did not accept the current-user interactive task.",
                "Do not elevate. If policy blocks per-user tasks, use the documented fail-closed manual desktop recovery.");
        }

        var finalReadback = _tasks.Read(proposed.TaskName);
        if (!InteractiveTaskDefinitions.Matches(finalReadback, definition))
        {
            // A mismatched task may have been altered concurrently. Preserve it
            // for review but remove our registration so it cannot launch.
            _ = InteractiveLaunchRegistrationStore.TryDelete(proposed.RegistrationId, _registrationPath, out _);
            return Error("TASK_DEFINITION_MISMATCH", "Task Scheduler readback did not match the requested fixed definition.");
        }

        return new(true, "configured", TaskName: proposed.TaskName);
    }

    internal InteractiveLaunchOperationResult Status(string appPath, string dataDir)
    {
        var userSid = _currentUserSid();
        if (!InteractiveLaunchRegistrationStore.TryLoad(_registrationPath, out var registration, out var failure) || registration is null)
            return Error(failure == "INTERACTIVE_LAUNCH_REGISTRATION_MISSING" ? "INTERACTIVE_LAUNCH_NOT_ENROLLED" : failure,
                "No valid per-user interactive launch registration is available.",
                failure == "INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED"
                    ? "This legacy cross-account registration is not supported. No task was changed; review and revoke any legacy task from the target user's desktop."
                    : "Use the host's authorized user-desktop execution surface first. Same-user task setup is only an advanced recovery option.");

        var validation = InteractiveLaunchRegistrationStore.Validate(
            registration, registration.RegistrationId, userSid, appPath, dataDir);
        if (validation.Length != 0)
            return Error(validation, "The current user, App path, binary, or data directory differs from the enrolled registration.",
                "Review the registration, then remove and re-enroll it from the unlocked desktop if the change is intentional.");

        var readback = _tasks.Read(registration.TaskName);
        if (!readback.Exists)
            return Error(readback.FailureCode == "TASK_NOT_FOUND" ? "TASK_NOT_FOUND" : "TASK_SCHEDULER_UNAVAILABLE",
                "The registered Task Scheduler task is missing or unavailable.");
        if (!InteractiveTaskDefinitions.Matches(readback, InteractiveTaskDefinitions.For(registration)))
            return Error("TASK_DEFINITION_MISMATCH", "Task Scheduler no longer contains the exact enrolled user, App path, arguments, and data binding.");
        return new(true, "configured", TaskName: registration.TaskName);
    }

    internal InteractiveLaunchOperationResult Remove()
    {
        var userSid = _currentUserSid();
        if (!InteractiveLaunchRegistrationStore.TryLoad(_registrationPath, out var registration, out var failure) || registration is null)
            return Error(failure == "INTERACTIVE_LAUNCH_REGISTRATION_MISSING" ? "INTERACTIVE_LAUNCH_NOT_ENROLLED" : failure,
                failure == "INTERACTIVE_CROSS_ACCOUNT_UNSUPPORTED"
                    ? "This legacy cross-account registration cannot be removed by this CLI; no real task was changed. Review and revoke it from the target user's desktop."
                    : "No valid per-user registration was found to remove.");
        if (!string.Equals(registration.UserSid, userSid, StringComparison.Ordinal) ||
            !string.Equals(registration.TaskName, InteractiveLaunchRegistrationStore.CreateTaskName(userSid), StringComparison.Ordinal))
            return Error("INTERACTIVE_LAUNCH_USER_MISMATCH", "The registration belongs to a different user and was not changed.");

        var readback = _tasks.Read(registration.TaskName);
        if (readback.Exists)
        {
            if (!InteractiveTaskDefinitions.Matches(readback, InteractiveTaskDefinitions.For(registration)))
                return Error("TASK_DEFINITION_MISMATCH", "The task was modified and was not deleted; review it manually before removal.");
            if (!_tasks.TryDelete(registration.TaskName, out var deleteFailure))
                return Error(deleteFailure, "The matching Agent Recorder task could not be removed.");
        }
        else if (readback.FailureCode is not "TASK_NOT_FOUND" and not "")
        {
            return Error("TASK_SCHEDULER_UNAVAILABLE", "Task Scheduler could not be queried; registration was preserved.");
        }

        if (!InteractiveLaunchRegistrationStore.TryDelete(registration.RegistrationId, _registrationPath, out var registrationFailure))
            return Error(registrationFailure, "The task was removed, but the matching registration file could not be removed.");
        return new(true, "removed");
    }

    internal InteractiveLaunchDiagnostic Diagnose()
    {
        var observation = _observe();
        return new(
            observation.ProcessId,
            observation.ProcessUserSid,
            observation.ProcessSessionId,
            observation.ActiveSessionId,
            observation.WindowStation,
            observation.ThreadDesktop,
            observation.InputDesktop,
            observation.IsOnInteractiveDesktop ? "direct" :
                observation.CanBrokerToInteractiveDesktop
                    ? "task_scheduler_interactive_token" : "unavailable",
            observation.FailureCode);
    }

    private static InteractiveLaunchOperationResult Error(string code, string message, string? suggestedAction = null) =>
        new(false, "error", code, message, suggestedAction);

    private static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return GetTokenInformation(identity.Token, 20, out var elevation, sizeof(int)) && elevation != 0;
        }
        catch { return true; }
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token, int informationClass, out int information, int informationLength);

}
