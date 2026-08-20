using System;

namespace AgentRecorder.Capture;

/// <summary>
/// An immutable chapter mark on the trusted first-frame media timeline.
/// </summary>
public sealed record RecordingMark
{
    private const int MaxLabelScalars = 200;

    public long TMs { get; }
    public string Label { get; }
    public string Source { get; }

    public RecordingMark(long tMs, string label, string source)
    {
        if (tMs < 0)
            throw new ArgumentOutOfRangeException(nameof(tMs), "Mark time must be non-negative.");

        if (label is null)
            throw new ArgumentNullException(nameof(label));

        label = NormalizeAndValidateLabel(label);

        if (!string.Equals(source, "agent", StringComparison.Ordinal) &&
            !string.Equals(source, "hotkey", StringComparison.Ordinal))
            throw new ArgumentException("Mark source must be agent or hotkey.", nameof(source));

        TMs = tMs;
        Label = label;
        Source = source;
    }

    private static string NormalizeAndValidateLabel(string submittedLabel)
    {
        // Inspect the submitted value before Trim() so CR/LF/TAB/NUL and
        // other control characters cannot be erased at the edges.
        ValidateUnicodeAndControls(submittedLabel);

        var normalized = submittedLabel.Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("Mark label must not be empty.", nameof(submittedLabel));

        int scalarCount = CountUnicodeScalars(normalized);
        if (scalarCount > MaxLabelScalars)
            throw new ArgumentException("Mark label is too long.", nameof(submittedLabel));

        return normalized;
    }

    private static void ValidateUnicodeAndControls(string label)
    {
        for (int i = 0; i < label.Length; i++)
        {
            char current = label[i];
            if (char.IsControl(current))
                throw new ArgumentException("Mark label must not contain control characters.", nameof(label));

            if (char.IsHighSurrogate(current))
            {
                if (i + 1 >= label.Length || !char.IsLowSurrogate(label[i + 1]))
                    throw new ArgumentException("Mark label must contain valid Unicode text.", nameof(label));
                i++;
            }
            else if (char.IsLowSurrogate(current))
            {
                throw new ArgumentException("Mark label must contain valid Unicode text.", nameof(label));
            }
        }
    }

    private static int CountUnicodeScalars(string label)
    {
        int scalarCount = 0;
        for (int i = 0; i < label.Length; i++)
        {
            if (char.IsHighSurrogate(label[i]))
                i++;
            scalarCount++;
        }
        return scalarCount;
    }
}
