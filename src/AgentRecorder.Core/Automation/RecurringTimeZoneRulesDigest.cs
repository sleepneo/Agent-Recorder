using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

/// <summary>
/// Computes the persisted identity of the rules used to resolve a recurring
/// schedule.  Only canonical scalar fields are included: localized display
/// text, current time, and TimeZoneInfo.ToString() are intentionally absent.
/// </summary>
public static class RecurringTimeZoneRulesDigest
{
    public const int CanonicalVersion = 1;
    public const string Prefix = "recurring-timezone-rules/v1:";

    public static string Compute(TimeZoneInfo timeZoneInfo)
    {
        ArgumentNullException.ThrowIfNull(timeZoneInfo);

        var bytes = new List<byte>(512);
        AppendString(bytes, "recurring-timezone-rules/v1");
        AppendInt32(bytes, CanonicalVersion);
        AppendString(bytes, timeZoneInfo.Id);
        AppendInt64(bytes, timeZoneInfo.BaseUtcOffset.Ticks);
        AppendInt32(bytes, timeZoneInfo.SupportsDaylightSavingTime ? 1 : 0);

        var rules = timeZoneInfo.GetAdjustmentRules()
            .OrderBy(rule => rule.DateStart.Ticks)
            .ThenBy(rule => rule.DateEnd.Ticks)
            .ToArray();
        AppendInt32(bytes, rules.Length);
        foreach (var rule in rules)
        {
            AppendDateTime(bytes, rule.DateStart);
            AppendDateTime(bytes, rule.DateEnd);
            AppendInt64(bytes, rule.DaylightDelta.Ticks);
            AppendInt64(bytes, rule.BaseUtcOffsetDelta.Ticks);
            AppendTransition(bytes, rule.DaylightTransitionStart);
            AppendTransition(bytes, rule.DaylightTransitionEnd);
        }

        return Prefix + Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
    }

    private static void AppendTransition(List<byte> bytes, TimeZoneInfo.TransitionTime transition)
    {
        AppendInt32(bytes, transition.IsFixedDateRule ? 1 : 0);
        AppendInt32(bytes, transition.Month);
        AppendInt32(bytes, transition.Week);
        AppendInt32(bytes, transition.Day);
        AppendInt32(bytes, (int)transition.DayOfWeek);
        AppendDateTime(bytes, transition.TimeOfDay);
    }

    private static void AppendDateTime(List<byte> bytes, DateTime value) => AppendInt64(bytes, value.Ticks);

    private static void AppendString(List<byte> bytes, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        AppendInt32(bytes, utf8.Length);
        bytes.AddRange(utf8);
    }

    private static void AppendInt32(List<byte> bytes, int value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void AppendInt64(List<byte> bytes, long value)
    {
        unchecked
        {
            AppendInt32(bytes, (int)(value >> 32));
            AppendInt32(bytes, (int)value);
        }
    }
}
