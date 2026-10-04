using System;

namespace AgentRecorder.Infrastructure;

public sealed record InteractiveLaunchRequest(
    bool IsBrokered,
    string RegistrationId,
    string RequestId);

/// <summary>
/// Private command-line protocol between AgentRecorder.Cli and the fixed App
/// executable. It intentionally has no generic executable or argument fields.
/// </summary>
public static class InteractiveLaunchProtocol
{
    public const string DirectStartArgument = "--agent-recorder-direct-start";
    public const string BrokerStartArgument = "--agent-recorder-interactive-launch";
    public const string TaskRequestPlaceholder = "$(Arg0)";

    public static bool TryParseAppArguments(
        string[] args,
        out InteractiveLaunchRequest? request,
        out string failureCode)
    {
        request = null;
        failureCode = string.Empty;
        if (args.Length == 0)
            return true;

        if (args.Length == 2 && args[0] == DirectStartArgument && IsId(args[1]))
        {
            request = new(false, string.Empty, args[1]);
            return true;
        }

        if (args.Length == 3 && args[0] == BrokerStartArgument && IsId(args[1]) && IsId(args[2]))
        {
            request = new(true, args[1], args[2]);
            return true;
        }

        failureCode = "UNTRUSTED_APP_ARGUMENTS";
        return false;
    }

    public static bool IsId(string? value) =>
        value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static string CreateDirectArguments(string requestId) =>
        $"{DirectStartArgument} {requestId}";

    public static string CreateTaskArguments(string registrationId) =>
        $"{BrokerStartArgument} {registrationId} {TaskRequestPlaceholder}";
}
