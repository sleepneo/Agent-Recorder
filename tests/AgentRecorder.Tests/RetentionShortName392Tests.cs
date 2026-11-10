using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionShortName392Tests
{
    private const string NativeParentVariable = "TASK392_NATIVE_PARENT";

    public sealed class NativeShortNameFactAttribute : FactAttribute
    {
        public NativeShortNameFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NativeParentVariable)))
                Skip = "Supply a disposable Windows fixture parent on a volume generating 8.3 names.";
        }
    }

    [NativeShortNameFact]
    public async Task RealShortNameProtectsCandidateOnlyPreviewAndByIdWithoutAnotherLink()
    {
        using var local = NativeFixture();
        var alias = VerifyRealAlias(local);
        var otherParent = Path.Combine(local.Db.Root, "other");
        Directory.CreateDirectory(otherParent);
        var otherPath = Path.Combine(otherParent, Path.GetFileName(alias));
        File.WriteAllBytes(otherPath, local.Bytes);
        Assert.NotEqual(local.Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(otherPath).After!.Identity);
        AddOldReference(local, "different-object", otherPath);
        var preview = new RetentionPreviewQueryService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None);
        var evaluation = new RetentionEvaluationService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None);
        var cutoff = At.AddHours(1);
        Assert.Equal("age_rule_match", Assert.Single(preview.GetPage(Sid, Session, 1, cutoff).Items).ReasonCode);
        var before = await evaluation.ReadAsync([local.Row.RecordingId], cutoff, Sid, Session);
        Assert.Equal("age_rule_match", Assert.Single(before!.Page!.Items).ReasonCode);

        AddOldReference(local, "legacy", alias);
        var snapshot = local.Db.Snapshot();
        Assert.Equal("shared_output_path", Assert.Single(preview.GetPage(Sid, Session, 1, cutoff).Items).ReasonCode);
        var after = await evaluation.ReadAsync([local.Row.RecordingId], cutoff, Sid, Session);
        Assert.Equal("shared_output_path", Assert.Single(after!.Page!.Items).ReasonCode);
        Assert.Equal(snapshot, local.Db.Snapshot());
    }

    [NativeShortNameFact]
    public async Task RealReferenceAddedAfterApprovalStopsDisposition()
    {
        using var local = NativeFixture();
        var alias = VerifyRealAlias(local);
        using var service = local.Service();
        var pending = service.Request(local.Row.RecordingId);
        var permit = await local.Approve(service, pending);
        AddOldReference(local, "late", alias);
        var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(permit, local.Row.RecordingId), pending);
        Assert.True(result.ApprovalConsumed);
        Assert.Equal(LocalDeletionReason.ScopeRejected, result.Reason);
        Assert.Equal(LocalDeletionProgress.NotAttempted, result.Progress);
        Assert.Equal(0, local.Native.Calls);
        Assert.Equal(local.Bytes, File.ReadAllBytes(local.Media));
        Assert.Equal(local.Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(alias).After!.Identity);
    }

    [Fact]
    public async Task UnknownOrChangingLeafBlocksWhileKnownAbsenceAllowsTheExistingChecks()
    {
        foreach (var kind in new[] { "absent", "unrelated", "unknown", "changed", "appeared" })
        {
            using var local = new LocalRetentionDeletionFixture();
            var reads = 0;
            local.Files.Api.ShortLeaf = () => kind switch
            {
                "absent" => "",
                "unrelated" => "OTHER.MP4",
                "unknown" => null,
                "appeared" => ++reads == 1 ? "" : "SECOND.MP4",
                _ => ++reads == 1 ? "FIRST.MP4" : "SECOND.MP4"
            };
            using var service = local.Service();
            var pending = service.Request(local.Row.RecordingId);
            var permit = await local.Approve(service, pending);
            var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(permit, local.Row.RecordingId), pending);
            var allowed = kind is "absent" or "unrelated";
            Assert.Equal(allowed ? 1 : 0, local.Native.Calls);
            Assert.Equal(allowed ? LocalDeletionProgress.DispositionAccepted : LocalDeletionProgress.NotAttempted, result.Progress);
        }
    }

    [Theory]
    [InlineData("", "age_rule_match")]
    [InlineData(null, "metadata_unavailable")]
    [InlineData("..\\x.mp4", "metadata_unavailable")]
    public void PreviewDistinguishesConfirmedAbsenceAndUnusableObservation(string? leaf, string expected)
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        f.Seed("candidate");
        var sample = new RetentionPreviewQueryTests.MetadataFake();
        f.Files.Observation = path => sample.Observe(path)! with { ShortLeaf = leaf };
        Assert.Equal(expected, Assert.Single(f.Page().Items).ReasonCode);
    }

    private static LocalRetentionDeletionFixture NativeFixture() => new(real: true,
        fixtureParent: Environment.GetEnvironmentVariable(NativeParentVariable)!,
        mediaName: "A disposable long recording file for alias evidence.mp4");

    private static string VerifyRealAlias(LocalRetentionDeletionFixture local)
    {
        var native = WindowsReadOnlyFileMetadataProbe.Instance;
        var observation = native.ObserveRetention(local.Media);
        Assert.Equal(FileObservationOutcome.Sampled, observation.Outcome);
        Assert.True(WindowsShortFileName.TryGetAliasPath(local.Media, observation.ShortLeaf, out var alias));
        Assert.NotNull(alias);
        Assert.Equal(local.Videos, Path.GetDirectoryName(alias));
        var throughAlias = native.Observe(alias);
        Assert.Equal(local.Anchor.Identity, throughAlias.After!.Identity);
        Assert.Equal(1u, observation.After!.Links);
        Assert.Equal(1u, throughAlias.After.Links);
        Assert.Equal(local.Media, throughAlias.AfterPath);
        return alias;
    }

    private static void AddOldReference(LocalRetentionDeletionFixture local, string id, string path) =>
        local.Db.Ordinary.Register(local.Db.Recording(id, At.AddMinutes(-1), completed: true) with { OutputPath = path });
}
