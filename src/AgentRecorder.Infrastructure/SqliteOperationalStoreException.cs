namespace AgentRecorder.Infrastructure;

public sealed class SqliteOperationalStoreException : InvalidOperationException
{
    public SqliteOperationalStoreException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A stable error code is required.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }
}
