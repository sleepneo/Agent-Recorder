using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionDataRootShortName393Tests(ITestOutputHelper output)
{
    public sealed class NativeDirectoryTheoryAttribute : TheoryAttribute
    {
        public NativeDirectoryTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TASK393_NATIVE_PARENT")))
                Skip = "Supply an owned disposable Windows parent generating real directory short names.";
        }
    }

    [NativeDirectoryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootAliasProtectsPreviewByIdAndLateApprovedDisposition(bool shortLeaf)
    {
        using var local = new LocalRetentionDeletionFixture(real: true,
            fixtureParent: Environment.GetEnvironmentVariable("TASK393_NATIVE_PARENT"),
            mediaName: "Long disposable recording filename.mp4");
        var native = WindowsOriginalFileHoldNative.Instance;
        using var root = native.Open(local.Db.Root, 0x80, 7, 3, 0x02200000, out var error);
        Assert.False(root.IsInvalid, error.ToString());
        var directoryLeaf = native.ReadShortLeaf(root);
        Assert.True(WindowsShortFileName.TryGetAliasPath(local.Db.Root, directoryLeaf, out var rootAlias));
        Assert.NotNull(rootAlias);
        using var alternate = native.Open(rootAlias, 0x80, 7, 3, 0x02200000, out _);
        Assert.True(native.Identity(root, out var originalId, out _));
        Assert.True(native.Identity(alternate, out var alternateId, out _));
        Assert.Equal(originalId, alternateId);
        root.Dispose();
        alternate.Dispose();
        var observation = WindowsReadOnlyFileMetadataProbe.Instance.ObserveRetention(local.Media);
        Assert.Equal(directoryLeaf, observation.DataRootShortLeaf);
        var alias = Path.Combine(rootAlias, "Videos", shortLeaf ? observation.ShortLeaf! : Path.GetFileName(local.Media));
        var aliasObservation = WindowsReadOnlyFileMetadataProbe.Instance.Observe(alias);
        Assert.Equal(local.Anchor.Identity, aliasObservation.After!.Identity);
        Assert.Equal(1u, aliasObservation.After.Links);
        Assert.Equal(local.Media, aliasObservation.AfterPath);

        var other = Path.Combine(Path.GetDirectoryName(local.Db.Root)!, "other-" + Guid.NewGuid().ToString("N"), "Videos");
        Directory.CreateDirectory(other);
        var otherFile = Path.Combine(other, Path.GetFileName(local.Media));
        File.WriteAllBytes(otherFile, local.Bytes);
        Assert.NotEqual(local.Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(otherFile).After!.Identity);
        AddReference(local, "other", otherFile);
        var preview = new RetentionPreviewQueryService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None);
        var evaluation = new RetentionEvaluationService(local.Db.Query, local.Db.Root, TestRuntimeOutputUse.None);
        Assert.Equal("age_rule_match", Assert.Single(preview.GetPage(Sid, Session, 1, At.AddHours(1)).Items).ReasonCode);
        var before = await evaluation.ReadAsync([local.Row.RecordingId], At.AddHours(1), Sid, Session);
        Assert.Equal("age_rule_match", Assert.Single(before!.Page!.Items).ReasonCode);

        using var service = local.Service();
        var pending = service.Request(local.Row.RecordingId);
        var permit = await local.Approve(service, pending);
        AddReference(local, "late", alias);
        var snapshot = local.Db.Snapshot();
        Assert.Equal("shared_output_path", Assert.Single(preview.GetPage(Sid, Session, 1, At.AddHours(1)).Items).ReasonCode);
        var after = await evaluation.ReadAsync([local.Row.RecordingId], At.AddHours(1), Sid, Session);
        Assert.Equal("shared_output_path", Assert.Single(after!.Page!.Items).ReasonCode);
        Assert.Equal(snapshot, local.Db.Snapshot());
        var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(permit, local.Row.RecordingId), pending);
        Assert.Equal(LocalDeletionProgress.NotAttempted, result.Progress);
        Assert.Equal(LocalDeletionReason.ScopeRejected, result.Reason);
        Assert.Equal(0, local.Native.Calls);
        Assert.Equal(local.Bytes, File.ReadAllBytes(local.Media));
        Assert.Equal(local.Anchor.Identity, WindowsReadOnlyFileMetadataProbe.Instance.Observe(alias).After!.Identity);
        output.WriteLine($"root={local.Db.Root}\nrootAlias={rootAlias}\nrootId={originalId.VolumeSerialNumber}/{originalId.IdHigh}/{originalId.IdLow}\nalias={alias}\nfileId={local.Anchor.Identity}\nprogress={result.Progress} calls={local.Native.Calls}");
    }

    [Theory]
    [InlineData("absent", true)]
    [InlineData("unknown", false)]
    [InlineData("changed", false)]
    [InlineData("appeared", false)]
    [InlineData("identity_changed", false)]
    public async Task DirectoryUncertaintyCannotSubmit(string kind, bool allowed)
    {
        using var local = new LocalRetentionDeletionFixture();
        using var service = local.Service();
        var pending = service.Request(local.Row.RecordingId);
        var permit = await local.Approve(service, pending);
        var reads = 0;
        local.Files.Api.DirectoryShortLeaf = () => kind switch
        {
            "absent" => "",
            "unknown" => null,
            "appeared" => ++reads == 1 ? "" : "SECOND",
            _ => ++reads == 1 ? "FIRST" : "SECOND"
        };
        if (kind == "identity_changed")
            local.Files.Api.IdentityValue = (node, _, value) =>
                node.Path == local.Db.Root ? value with { IdLow = value.IdLow + 1 } : value;
        var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(permit, local.Row.RecordingId), pending);
        Assert.Equal(allowed ? 1 : 0, local.Native.Calls);
        Assert.Equal(allowed ? LocalDeletionProgress.DispositionAccepted : LocalDeletionProgress.NotAttempted, result.Progress);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("..\\invalid")]
    public void UnusableDirectoryObservationProtectsPreview(string? directoryLeaf)
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        f.Seed("candidate");
        var sample = new RetentionPreviewQueryTests.MetadataFake();
        f.Files.Observation = p => sample.Observe(p)! with { DataRootShortLeaf = directoryLeaf };
        Assert.Equal("metadata_unavailable", Assert.Single(f.Page().Items).ReasonCode);
    }

    [Fact]
    public void EveryCombinationUsesOnlyTheFixedRootAndLeafAndAtMostThreeIndexedBatches()
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        var candidates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        for (var i = 0; i < 100; i++)
        {
            var path = Path.Combine(f.Videos, "long recording " + i + ".mp4");
            Assert.True(WindowsShortFileName.TryGetAliasPaths(path, "LEAF.MP4", "ROOT", out var aliases));
            Assert.Equal(3, aliases.Count);
            Assert.All(aliases, alias => Assert.EndsWith("Videos\\" + Path.GetFileName(alias), alias));
            candidates.Add("candidate" + i, aliases);
        }
        var commands = 0;
        f.Db.Query.RetentionPathCommandForTests = (c, t, sql, args) =>
        {
            commands++;
            Assert.Equal(200, args.Count);
            using var command = c.CreateCommand();
            command.Transaction = t;
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var p in args) command.Parameters.AddWithValue(p.Key, p.Value);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            Assert.DoesNotContain(plan, p => p.Contains("SCAN referenced"));
            Assert.Contains(plan, p => p.Contains("SEARCH referenced USING INDEX idx_ordinary_recording_history_shared_output_path"));
            if (commands == 1) output.WriteLine(string.Join("\n", plan));
        };
        Assert.All(f.Db.Query.ReadShortNameReferences(candidates, CancellationToken.None).Values,
            value => Assert.False(value.Shared || value.Planned));
        Assert.Equal(3, commands);
        Assert.Throws<InvalidDataException>(() => f.Db.Query.ReadShortNameReferences(
            new Dictionary<string, IReadOnlyList<string>> { ["bad"] = new[] { "a", "b", "c", "d" } }, CancellationToken.None));
    }

    private static void AddReference(LocalRetentionDeletionFixture local, string id, string path) =>
        local.Db.Ordinary.Register(local.Db.Recording(id, At.AddMinutes(-1), completed: true) with { OutputPath = path });
}
