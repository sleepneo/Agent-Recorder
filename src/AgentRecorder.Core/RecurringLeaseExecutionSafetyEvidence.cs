using AgentRecorder.Core.Automation;

namespace AgentRecorder.Core;

/// <summary>
/// Core-side immutable projection of the durable global unattended safety
/// state. Persistence maps the exact SQLite row into this value while its
/// transaction is held.
/// </summary>
internal sealed record RecurringLeaseExecutionSafetyEvidence(
    UnattendedModeStatus UnattendedMode,
    bool StopAllApplied,
    DateTimeOffset? StopAllAppliedAtUtc,
    DateTimeOffset? UnattendedEnabledAtUtc,
    long Version);
