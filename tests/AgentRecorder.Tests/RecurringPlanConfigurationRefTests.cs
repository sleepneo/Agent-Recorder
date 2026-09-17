using AgentRecorder.Core.Automation;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringPlanConfigurationRefTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly string ScheduleDigest = "recurring-schedule/v1:" + "a".PadRight(64, 'a');
    private static readonly string RulesDigest = RecurringTimeZoneRulesDigest.Prefix + "b".PadRight(64, 'b');

    [Fact]
    public void ConfigurationDigestIsDeterministicAndFieldSensitive()
    {
        var profileRef = new ProfileRef("profile", 1, RecurringFixedRegionProfileVersion.DigestPrefix + "c".PadRight(64, 'c'));
        var first = new RecurringPlanConfigurationRef("plan", 1, ScheduleDigest, RulesDigest, profileRef);
        var same = new RecurringPlanConfigurationRef("plan", 1, ScheduleDigest, RulesDigest, profileRef);

        Assert.Equal(first.ConfigurationDigest, same.ConfigurationDigest);
        Assert.StartsWith(RecurringPlanConfigurationRef.DigestPrefix, first.ConfigurationDigest, StringComparison.Ordinal);
        Assert.Equal(RecurringPlanConfigurationRef.DigestPrefix.Length + 64, first.ConfigurationDigest.Length);

        var variants = new[]
        {
            new RecurringPlanConfigurationRef("plan-2", 1, ScheduleDigest, RulesDigest, profileRef),
            new RecurringPlanConfigurationRef("plan", 2, ScheduleDigest, RulesDigest, profileRef),
            new RecurringPlanConfigurationRef("plan", 1, ScheduleDigest[..^1] + "d", RulesDigest, profileRef),
            new RecurringPlanConfigurationRef("plan", 1, ScheduleDigest, RulesDigest[..^1] + "e", profileRef),
            new RecurringPlanConfigurationRef("plan", 1, ScheduleDigest, RulesDigest, new ProfileRef("profile-2", 1, profileRef.ProfileDigest)),
        };

        Assert.All(variants, variant => Assert.NotEqual(first.ConfigurationDigest, variant.ConfigurationDigest));
    }

    [Fact]
    public void ConfigurationReferenceRejectsDefaultAndMalformedRehydration()
    {
        var defaultRef = Assert.Throws<Phase3DomainException>(() =>
            new RecurringPlanConfigurationRef("plan", 1, ScheduleDigest, RulesDigest, default));
        Assert.Equal(RecurringFixedRegionProfileReasonCodes.ProfileIdInvalid, defaultRef.ReasonCode);

        var profileRef = new ProfileRef("profile", 1, RecurringFixedRegionProfileVersion.DigestPrefix + "c".PadRight(64, 'c'));
        var configuration = new RecurringPlanConfigurationRef("plan", 1, ScheduleDigest, RulesDigest, profileRef);
        var mismatch = Assert.Throws<Phase3DomainException>(() =>
            RecurringPlanConfigurationRef.Rehydrate(
                configuration.PlanId,
                configuration.ScheduleRevision,
                configuration.ScheduleDigest,
                configuration.TimeZoneRulesDigest,
                configuration.ProfileRef,
                configuration.ConfigurationDigest[..^1] + (configuration.ConfigurationDigest[^1] == '0' ? '1' : '0')));
        Assert.Equal(RecurringPlanConfigurationReasonCodes.ConfigurationDigestMismatch, mismatch.ReasonCode);

        var malformed = Assert.Throws<Phase3DomainException>(() =>
            new RecurringPlanConfigurationRef("plan", 0, ScheduleDigest, RulesDigest, profileRef));
        Assert.Equal(RecurringPlanConfigurationReasonCodes.RevisionInvalid, malformed.ReasonCode);
    }
}
