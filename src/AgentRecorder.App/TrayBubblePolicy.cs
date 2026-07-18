namespace AgentRecorder.App;

/// <summary>
/// Categories of tray balloon tips that <see cref="TrayContext" /> may show.
/// </summary>
public enum BubbleType
{
    RecordingStarted,
    ConfirmationWaiting,
    Error
}

/// <summary>
/// Decides whether a given shell tray balloon is allowed to show.
/// The policy is intentionally tiny and free of Win32 dependencies so it can be unit tested.
/// </summary>
public interface ITrayBubblePolicy
{
    bool AllowShowBubble(BubbleType type, int activeRecordingCount);
}

/// <summary>
/// Default production policy:
/// - "recording started" and "confirmation waiting" balloons are never shown;
///   recording state is conveyed by the indicator / tray icon, and confirmation
///   state is conveyed by the front-most confirmation form, tray menu and API;
/// - error balloons are suppressed while any recording is active, so they do not
///   pollute the captured video.
/// Suppressed balloons are not queued or replayed later.
/// </summary>
public sealed class TrayBubblePolicy : ITrayBubblePolicy
{
    public bool AllowShowBubble(BubbleType type, int activeRecordingCount) => type switch
    {
        BubbleType.RecordingStarted => false,
        BubbleType.ConfirmationWaiting => false,
        BubbleType.Error => activeRecordingCount == 0,
        _ => false
    };
}
