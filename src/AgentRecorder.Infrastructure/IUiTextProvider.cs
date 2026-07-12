namespace AgentRecorder.Infrastructure;

/// <summary>
/// Provides localized user-visible strings for the Agent Recorder UI.
/// Implementations must be thread-safe for read access.
/// </summary>
public interface IUiTextProvider
{
    /// <summary>
    /// Currently selected language.
    /// </summary>
    UiLanguage Language { get; }

    /// <summary>
    /// Returns the localized string for the given key.
    /// If the key is missing, returns the key itself so the UI still shows
    /// a searchable token rather than an empty label.
    /// </summary>
    string Get(string key);

    /// <summary>
    /// Returns the localized string and formats it with the supplied arguments.
    /// </summary>
    string Format(string key, params object?[] args);
}
