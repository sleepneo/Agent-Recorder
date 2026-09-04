namespace AgentRecorder.Persistence;

/// <summary>
/// Stable persistence boundary error. The message is intentionally generic so
/// identifiers and other persisted values never become diagnostic payloads.
/// </summary>
public sealed class Phase3PersistenceException : InvalidOperationException
{
    public Phase3PersistenceException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A stable persistence error code is required.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }
}
