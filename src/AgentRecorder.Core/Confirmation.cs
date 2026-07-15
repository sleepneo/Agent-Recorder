using System;
using System.Threading;

namespace AgentRecorder.Core;

/// <summary>
/// Represents a pending user confirmation. The decision (approve/reject/expire)
/// is atomically claimable so that only one of the user callback, timeout, or
/// duplicate callback paths can transition the confirmation out of pending.
/// </summary>
public sealed class Confirmation
{
    public string Id { get; } = "confirm_" + Guid.NewGuid().ToString("N")[..12];
    public string RecordingId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public int TimeoutSeconds { get; set; } = 60;

    // 0 = pending, 1 = decided. Used with Interlocked.CompareExchange so that
    // exactly one caller can win the decision race.
    private int _decisionState;

    private string _status = "pending";

    /// <summary>
    /// Current confirmation status. Setter is retained for compatibility with
    /// existing serialization/tests, but production state transitions should
    /// use <see cref="TryDecide"/>.
    /// </summary>
    public string Status
    {
        get => _status;
        set => _status = value;
    }

    /// <summary>
    /// Returns true if a decision has already been atomically claimed.
    /// </summary>
    public bool IsDecided => Interlocked.CompareExchange(ref _decisionState, 1, 1) == 1;

    /// <summary>
    /// Atomically claims the decision for this confirmation. Only the first
    /// call from pending state succeeds and sets <see cref="Status"/>.
    /// Subsequent calls (duplicate callbacks, timeout after approval, etc.)
    /// return false and must not modify recording state or emit events.
    /// </summary>
    public bool TryDecide(string status)
    {
        if (Interlocked.CompareExchange(ref _decisionState, 1, 0) != 0)
            return false;

        _status = status;
        return true;
    }
}
