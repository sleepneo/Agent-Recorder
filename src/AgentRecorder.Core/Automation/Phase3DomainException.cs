namespace AgentRecorder.Core.Automation;

/// <summary>
/// A deterministic domain contract violation. ReasonCode is stable and suitable
/// for logs and future persistence; it is deliberately not a localized message.
/// </summary>
public sealed class Phase3DomainException : ArgumentException
{
    public Phase3DomainException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = string.IsNullOrWhiteSpace(reasonCode)
            ? throw new ArgumentException("A stable reason code is required.", nameof(reasonCode))
            : reasonCode;
    }

    public string ReasonCode { get; }
}
