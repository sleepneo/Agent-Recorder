using AgentRecorder.Core;
using AgentRecorder.Persistence;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RetentionDerivedBundle391Tests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("missing", "shared_output_path")]
    [InlineData("explicit", "shared_output_path")]
    [InlineData("not_applicable", "age_rule_match")]
    public async Task PreviewAndByIdDistinguishLegalHistoryStates(string registration, string expected)
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        var media = Path.Combine(f.Videos, "legacy.MP4");
        var candidate = f.Seed("candidate", path: Path.Combine(f.Videos, "LEGACY.bundle", "deep", "out.mp4"));
        AddOwner(f.Db, media, registration);
        var before = f.Db.Snapshot();
        var page = f.Page();
        var evaluation = RetentionEvaluation387Tests.Service(f);
        var result = await evaluation.ReadAsync([candidate.RecordingId], f.Before, Sid, Session);
        Assert.Equal(new[] { expected, expected }, new[] {
            page.Items.Single(i => i.EntryId == "ordinary:candidate").ReasonCode,
            Assert.Single(result!.Page!.Items).ReasonCode
        });
        Assert.Equal(before, f.Db.Snapshot());
        if (registration != "not_applicable") RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Theory]
    [InlineData("legacy.bundle", true)]
    [InlineData("legacy.bundle\\inside.mp4", true)]
    [InlineData("legacy.bundle-old\\inside.mp4", false)]
    [InlineData("other\\legacy.bundle\\inside.mp4", false)]
    [InlineData("movie.bundle\\inside.mp4", false)]
    public void OnlyExactMp4DerivedDirectoryAndItsDescendantsMatch(string relativePath, bool expected)
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        AddOwner(f.Db, Path.Combine(f.Videos, "legacy.mp4"), "missing");
        AddOwner(f.Db, Path.Combine(f.Videos, "movie.mkv"), "missing", "other");
        using var connection = f.Db.Store.OpenReadOnlyConnection();
        using var transaction = connection.BeginTransaction();
        var candidates = new Dictionary<string, string?> { ["candidate"] = Path.Combine(f.Videos, relativePath) };
        Assert.Equal(expected, BundleNamespaceReferenceStorage.Read(connection, transaction, candidates)["candidate"]);
        Assert.Equal(expected, BundleNamespaceReferenceStorage.Read(connection, transaction, candidates)["candidate"]);
        RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Theory]
    [InlineData("wrong_media")]
    [InlineData("wrong_tuple")]
    [InlineData("unknown_status")]
    public async Task InvalidOwnerFactsCannotExemptTheDerivedScope(string scenario)
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        var media = Path.Combine(f.Videos, "legacy.mp4");
        var candidate = f.Seed("candidate", path: Path.Combine(f.Videos, "legacy.bundle", "out.mp4"));
        AddOwner(f.Db, media, "not_applicable");
        f.Db.Corrupt(scenario switch
        {
            "wrong_media" => "UPDATE ordinary_recording_bundles SET media_output_path='wrong' WHERE recording_id='legacy';",
            "wrong_tuple" => "UPDATE ordinary_recording_bundles SET error_code='wrong' WHERE recording_id='legacy';",
            _ => "UPDATE ordinary_recording_history SET status_code='unknown' WHERE recording_id='legacy';"
        });
        var result = await RetentionEvaluation387Tests.Service(f).ReadAsync([candidate.RecordingId], f.Before, Sid, Session);
        Assert.Equal("shared_output_path", Assert.Single(result!.Page!.Items).ReasonCode);
        RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Fact]
    public async Task OwnerAppearingAfterEvaluationAndApprovalStopsDisposition()
    {
        // Keep the file directly under the configured Videos directory, as
        // required by the existing deletion scope. Its ancestor is the data
        // root, which a historical sibling MP4 can claim in the same history DB.
        using var local = new LocalRetentionDeletionFixture(real: true, dataRootSuffix: ".bundle");
        var history = new UnifiedHistoryQueryService(local.Db.Store, local.Db.Ordinary);
        var before = await new RetentionEvaluationService(history, local.Db.Root, TestRuntimeOutputUse.None)
            .ReadAsync([local.Row.RecordingId], At.AddHours(1), Sid, Session);
        Assert.Equal("age_rule_match", Assert.Single(before!.Page!.Items).ReasonCode);
        using var service = local.Service();
        var pending = service.Request(local.Row.RecordingId);
        var proof = await local.Approve(service, pending);
        AddOwner(local.Db, Path.ChangeExtension(local.Db.Root, ".mp4"), "missing");
        var result = await LocalRetentionDeletionFixture.Finish(service.RequestDeletion(proof, local.Row.RecordingId), pending);
        Assert.Equal(LocalDeletionProgress.NotAttempted, result.Progress);
        Assert.Equal(RetentionGuardReason.SharedPathProtected, result.Retirement?.DatabaseReason);
        Assert.Equal(0, local.Native.Calls);
        Assert.Equal(local.Bytes, File.ReadAllBytes(local.Media));
    }

    [Fact]
    public void ExactIndexedLookupStopsAtOwnerBudget()
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        var media = Path.Combine(f.Videos, "legacy.mp4");
        var candidates = new Dictionary<string, string?> { ["candidate"] = Path.Combine(f.Videos, "legacy.bundle", "out.mp4") };
        var max = BundleNamespaceReferenceStorage.MaxLegacyOwnersPerKey;
        for (var i = 0; i < max; i++) AddOwner(f.Db, media, "not_applicable", "owner" + i);

        bool Read(bool inspect)
        {
            using var connection = f.Db.Store.OpenReadOnlyConnection();
            using var transaction = connection.BeginTransaction();
            return BundleNamespaceReferenceStorage.Read(connection, transaction, candidates, inspect ? (c, t, sql, args) =>
            {
                Assert.Contains($"LIMIT {max + 1}", sql);
                using var command = c.CreateCommand();
                command.Transaction = t;
                command.CommandText = "EXPLAIN QUERY PLAN " + sql;
                foreach (var arg in args) command.Parameters.AddWithValue(arg.Key, arg.Value);
                var plan = new List<string>();
                using var reader = command.ExecuteReader();
                while (reader.Read()) plan.Add(reader.GetString(3));
                Assert.Contains(plan, p => p.Contains("SEARCH ordinary_recording_history USING INDEX idx_ordinary_recording_history_shared_output_path") &&
                    p.Contains("output_path=?"));
                Assert.Contains(plan, p => p.Contains("SEARCH b USING INDEX sqlite_autoindex_ordinary_recording_bundles_1") &&
                    p.Contains("recording_id=?"));
                Assert.DoesNotContain(plan, p => p.Contains("SCAN ordinary_recording_history") || p.StartsWith("SCAN b"));
                output.WriteLine(string.Join("\n", plan));
            } : null)["candidate"];
        }

        Assert.False(Read(inspect: true));
        AddOwner(f.Db, media, "not_applicable", "overflow");
        Assert.True(Read(inspect: false));
    }

    private static void AddOwner(UnifiedHistoryTestDatabase db, string media, string registration, string id = "legacy")
    {
        var row = db.Recording(id, completed: true) with { OutputPath = media };
        row = row with
        {
            BundleRegistration = registration switch
            {
                "explicit" => new(OrdinaryRecordingBundleOperation.Create(id, media)),
                "not_applicable" => new(null),
                _ => null
            }
        };
        db.Ordinary.Register(row);
    }
}
