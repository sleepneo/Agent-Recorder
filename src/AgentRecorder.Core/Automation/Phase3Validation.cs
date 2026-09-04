namespace AgentRecorder.Core.Automation;

internal static class Phase3Validation
{
    public static string RequiredId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new Phase3DomainException("empty_id", $"{name} must not be empty.");
        }

        return value.Trim();
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new Phase3DomainException("non_utc_time", $"{name} must use UTC.");
        }

        return value;
    }

    public static void Window(DateTimeOffset startUtc, DateTimeOffset endUtc, string name)
    {
        if (startUtc >= endUtc)
        {
            throw new Phase3DomainException("invalid_time_window", $"{name} must have start before end.");
        }
    }

    public static int NonNegative(int value, string name)
    {
        if (value < 0)
        {
            throw new Phase3DomainException("negative_quota", $"{name} must not be negative.");
        }

        return value;
    }

    public static int Positive(int value, string name)
    {
        if (value <= 0)
        {
            throw new Phase3DomainException("positive_quota_required", $"{name} must be greater than zero.");
        }

        return value;
    }

    public static TimeSpan NonNegative(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero)
        {
            throw new Phase3DomainException("negative_duration", $"{name} must not be negative.");
        }

        return value;
    }

    public static TimeSpan Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new Phase3DomainException("positive_duration_required", $"{name} must be greater than zero.");
        }

        return value;
    }

    public static void Relation(bool condition, string reasonCode, string message)
    {
        if (!condition)
        {
            throw new Phase3DomainException(reasonCode, message);
        }
    }
}
