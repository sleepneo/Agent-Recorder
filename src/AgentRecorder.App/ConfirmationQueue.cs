using System;
using System.Collections.Generic;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.App;

/// <summary>
/// Represents a pending confirmation request in the queue.
/// </summary>
internal sealed class PendingConfirmationItem
{
    public string ConfirmationId { get; }
    public string RecordingId { get; }
    public object Summary { get; }
    public Action<ConfirmationDecision> Callback { get; }
    public int TimeoutSeconds { get; }
    public DateTime CreatedAtUtc { get; }
    public DateTime ExpiresAtUtc { get; }

    private int _callbackCalled;

    public PendingConfirmationItem(
        string confirmationId,
        string recordingId,
        object summary,
        Action<ConfirmationDecision> callback,
        int timeoutSeconds)
    {
        ConfirmationId = confirmationId;
        RecordingId = recordingId;
        Summary = summary;
        Callback = callback;
        TimeoutSeconds = timeoutSeconds;
        CreatedAtUtc = DateTime.UtcNow;
        ExpiresAtUtc = CreatedAtUtc.AddSeconds(timeoutSeconds);
        _callbackCalled = 0;
    }

    /// <summary>
    /// Invokes the callback with the given decision. Guarantees callback is only called once.
    /// Returns true if callback was invoked, false if already called.
    /// </summary>
    public bool InvokeCallback(ConfirmationDecision decision)
    {
        if (Interlocked.Exchange(ref _callbackCalled, 1) == 1)
            return false; // Already called

        Callback(decision);
        return true;
    }

    /// <summary>
    /// Returns true if callback has already been invoked.
    /// </summary>
    public bool CallbackCalled => _callbackCalled == 1;

    /// <summary>
    /// Returns true if the confirmation has expired based on local time.
    /// Note: Engine handles actual expiration; this is for UI display only.
    /// </summary>
    public bool IsExpiredLocal => DateTime.UtcNow > ExpiresAtUtc;
}

/// <summary>
/// Queue for pending confirmation requests. Thread-safe, testable, no WinForms dependency.
/// </summary>
internal sealed class ConfirmationQueue
{
    private readonly List<PendingConfirmationItem> _items = new();
    private readonly object _lock = new();

    /// <summary>
    /// Current confirmation being shown to user. Null if queue is empty.
    /// </summary>
    public PendingConfirmationItem? Current
    {
        get
        {
            lock (_lock)
            {
                return _items.Count > 0 ? _items[0] : null;
            }
        }
    }

    /// <summary>
    /// Total pending count, including current.
    /// </summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// Enqueue a new confirmation request. Does NOT auto-reject if there's already a pending item.
    /// </summary>
    public void Enqueue(PendingConfirmationItem item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    /// <summary>
    /// Resolves the current confirmation by removing it from the queue.
    /// Does NOT invoke the callback - returns the item so callback can be invoked outside the lock.
    /// Returns null if queue is empty or callback already called.
    /// </summary>
    public PendingConfirmationItem? ResolveCurrent()
    {
        lock (_lock)
        {
            if (_items.Count == 0) return null;
            var current = _items[0];
            if (current.CallbackCalled) return null;

            _items.RemoveAt(0);
            return current;
        }
    }

    /// <summary>
    /// Approve the current confirmation. Moves to next item if available.
    /// Returns true if approved, false if no current or callback already called.
    /// Callback is invoked outside the lock to avoid blocking queue operations.
    /// </summary>
    public bool ApproveCurrent(ConfirmationDecision? decision = null)
    {
        var item = ResolveCurrent();
        if (item == null) return false;
        item.InvokeCallback(decision ?? ConfirmationDecision.Approve());
        return true;
    }

    /// <summary>
    /// Reject the current confirmation. Moves to next item if available.
    /// Returns true if rejected, false if no current or callback already called.
    /// Callback is invoked outside the lock to avoid blocking queue operations.
    /// </summary>
    public bool RejectCurrent()
    {
        var item = ResolveCurrent();
        if (item == null) return false;
        item.InvokeCallback(ConfirmationDecision.Reject());
        return true;
    }

    /// <summary>
    /// Clear all pending items. Does NOT invoke callbacks for items that haven't been called.
    /// Used for SetAllIdle() or app exit to clean up UI state.
    /// </summary>
    /// <param name="invokeCallbacks">If true, invokes callback(false) for all uncalled items.
    /// If false, items are just removed without callback invocation (for engine-managed expiration).</param>
    public void Clear(bool invokeCallbacks = false)
    {
        List<PendingConfirmationItem> toCallback = new();

        lock (_lock)
        {
            if (invokeCallbacks)
            {
                foreach (var item in _items)
                {
                    if (!item.CallbackCalled)
                    {
                        toCallback.Add(item);
                    }
                }
            }
            _items.Clear();
        }

        // Invoke callbacks outside the lock
        foreach (var item in toCallback)
        {
            item.InvokeCallback(ConfirmationDecision.Reject());
        }
    }

    /// <summary>
    /// Get all pending items (for UI iteration). Returns a snapshot copy.
    /// </summary>
    public List<PendingConfirmationItem> GetAllItems()
    {
        lock (_lock)
        {
            return new List<PendingConfirmationItem>(_items);
        }
    }
}