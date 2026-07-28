using System;
using System.Text;

namespace AgentRecorder.Capture;

/// <summary>
/// Bounded in-memory log builder. Appends text up to a character limit, then
/// records a single truncation marker and discards further raw text.
/// </summary>
internal sealed class BoundedStringBuilder
{
    private readonly StringBuilder _sb = new();
    private readonly object _lock = new();
    private readonly int _maxChars;
    private bool _truncated;

    public BoundedStringBuilder(int maxChars)
    {
        _maxChars = maxChars > 0 ? maxChars : 0;
    }

    public void AppendLine(string? value)
    {
        if (value == null)
            return;

        lock (_lock)
        {
            if (_truncated)
                return;

            int remaining = _maxChars - _sb.Length;
            if (remaining <= 0)
            {
                MarkTruncatedLocked();
                return;
            }

            if (value.Length + 1 > remaining)
            {
                _sb.Append(value, 0, Math.Max(0, remaining - 1));
                _sb.AppendLine();
                MarkTruncatedLocked();
                return;
            }

            _sb.AppendLine(value);
        }
    }

    public void Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        lock (_lock)
        {
            if (_truncated)
                return;

            int remaining = _maxChars - _sb.Length;
            if (remaining <= 0)
            {
                MarkTruncatedLocked();
                return;
            }

            if (value.Length > remaining)
            {
                _sb.Append(value, 0, remaining);
                MarkTruncatedLocked();
                return;
            }

            _sb.Append(value);
        }
    }

    public override string ToString()
    {
        lock (_lock)
        {
            var result = _sb.ToString();
            if (_truncated)
                result += "\n[truncated=true]";
            return result;
        }
    }

    public bool IsTruncated
    {
        get { lock (_lock) return _truncated; }
    }

    public int Length
    {
        get { lock (_lock) return _sb.Length; }
    }

    private void MarkTruncatedLocked()
    {
        if (!_truncated)
        {
            _sb.AppendLine("[truncated=true]");
            _truncated = true;
        }
    }
}
