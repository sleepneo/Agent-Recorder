namespace AgentRecorder.AudioHelper;

/// <summary>
/// The approved audio input source for an AudioHelper capture session.
/// </summary>
internal enum AudioSourceKind
{
    Microphone,
    SystemLoopback
}

internal static class AudioSourceKindNames
{
    public const string Microphone = "microphone";
    public const string SystemLoopback = "system-loopback";

    public static string ToCliValue(AudioSourceKind sourceKind)
        => sourceKind == AudioSourceKind.SystemLoopback ? SystemLoopback : Microphone;

    public static bool TryParse(string value, out AudioSourceKind sourceKind)
    {
        if (string.Equals(value, Microphone, StringComparison.OrdinalIgnoreCase))
        {
            sourceKind = AudioSourceKind.Microphone;
            return true;
        }

        if (string.Equals(value, SystemLoopback, StringComparison.OrdinalIgnoreCase))
        {
            sourceKind = AudioSourceKind.SystemLoopback;
            return true;
        }

        sourceKind = default;
        return false;
    }
}
