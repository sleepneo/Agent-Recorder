using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

internal enum RecurringSetupTerminalResultStatus { Settled, AlreadyTerminal, Conflict, Rejected }

internal sealed record RecurringSetupTerminalResult(RecurringSetupTerminalResultStatus Status, string Reason)
{
    internal bool Changed => Status == RecurringSetupTerminalResultStatus.Settled;
}

internal sealed class RecurringPlanSetupTerminalService
{
    private readonly SqliteRecurringSetupTerminalTransaction _transaction;
    private readonly Func<DateTimeOffset?> _utcNow;
    private readonly object _clockSync = new();
    private DateTimeOffset? _lastUtcNow;

    internal RecurringPlanSetupTerminalService(
        SqliteOperationalStore store,
        Func<DateTimeOffset?>? utcNowForTest = null,
        Action<RecurringSetupTerminalFailurePoint>? failureHookForTest = null)
    {
        _transaction = new SqliteRecurringSetupTerminalTransaction(store, failureHookForTest);
        _utcNow = utcNowForTest ?? (() => DateTimeOffset.UtcNow);
    }

    internal RecurringSetupTerminalResult Settle(
        string intentId, string currentUserSid, string sessionBinding,
        RecurringSetupIntentStatus terminalKind, string reason)
    {
        if (!Canonical(intentId, RecurringSetupIntentSnapshot.MaximumIdLength) ||
            !Canonical(currentUserSid, RecurringSetupIntentSnapshot.MaximumBindingLength) ||
            !Canonical(sessionBinding, RecurringSetupIntentSnapshot.MaximumBindingLength) ||
            !RecurringSetupTerminalReasonPolicy.Allows(terminalKind, reason))
            return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_request_invalid");

        DateTimeOffset? now;
        try { now = _utcNow(); }
        catch { return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_clock_unavailable"); }
        if (now is null)
            return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_clock_unavailable");
        if (now.Value.Offset != TimeSpan.Zero)
            return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_time_not_utc");
        lock (_clockSync)
        {
            if (now < _lastUtcNow)
                return new(RecurringSetupTerminalResultStatus.Rejected, "recurring_setup_terminal_time_non_monotonic");
            _lastUtcNow = now;
        }

        return _transaction.Settle(intentId, currentUserSid, sessionBinding, terminalKind, reason, now.Value);
    }

    private static bool Canonical(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= maximum &&
        !value.Any(char.IsControl) && !value.Contains('/') && !value.Contains('\\');
}
