using System.Buffers.Binary;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class RetentionPublicationIdentityTests(ITestOutputHelper output)
{
    internal static void ForeignOrdinary(RetentionPreviewQueryTests.Fixture f, OrdinaryRecordingHistorySnapshot candidate,
        string id = "SECRET_OTHER", ObservedFileIdentity? identity = null, bool samePath = false)
    {
        var row = f.Db.Recording(id, f.Before.AddDays(2), completed: true) with
        { OutputPath = samePath ? candidate.OutputPath : Path.Combine(f.Videos, "foreign-" + id + ".mp4"), OutputSizeBytes = 999, BundleRegistration = new(null) };
        row = row with { PublicationReceipt = PublicationReceipt.IssueForTests(row.OutputPath, 999, identity ?? candidate.PublicationReceipt!.Anchor.Identity) };
        f.Db.Ordinary.Register(row); f.Db.Ordinary.Read(id);
    }

    internal static void PrivateForeign(UnifiedHistoryTestDatabase db, PublicationIdentityAnchor anchor,
        string source, string id, bool malformed = false, bool unknownKey = false)
    {
        var volume = new byte[8]; var file = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(volume, anchor.Identity.VolumeSerialNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(file, anchor.Identity.IdLow);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(8), anchor.Identity.IdHigh);
        // Explicit orphan/corrupt fixture. No production write bypass or trust
        // claim: only the saved tuple participates in the conservative EXISTS.
        var sql = source == "future" ?
            "INSERT INTO future_window_publication_anchors VALUES($id,'SECRET_RUN','C:\\SECRET_FOREIGN\\out.mp4',999,1,$source,$volume,$file);" :
            "INSERT INTO plan_media_publication_anchors VALUES($id,'SECRET_PLAN','SECRET_OCC',$kind,'SECRET_OWNER','C:\\SECRET_FOREIGN\\out.mp4','C:\\SECRET_FOREIGN\\out.mp4',999,1,$source,$volume,$file,999);";
        db.Corrupt(sql, ("$id", id), ("$kind", source), ("$source", malformed ? "BROKEN_BUSINESS" : "staging_copy_handle/v1"),
            ("$volume", unknownKey ? new byte[3] : volume), ("$file", file));
    }

    [Fact]
    public void OtherOrdinaryBeyondCutoffAndHighWaterProtectsDifferentPathAndHistoricalLength()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate");
        var first = f.Page(1); Assert.Equal("age_rule_match", Assert.Single(first.Items).ReasonCode);
        f.Files.Attributes.Clear(); f.Files.Observations.Clear(); f.Files.ProbeAttributes.Clear(); f.Files.Lengths.Clear();
        ForeignOrdinary(f, row); var before = UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath);
        var page = f.Page(1); var item = Assert.Single(page.Items);
        Assert.Equal("shared_publication_identity", item.ReasonCode); Assert.Equal("protected", item.Evaluation);
        Assert.True(item.AgeCandidate); Assert.Equal(row.OutputPath, item.RecordedPath); Assert.Equal(12, item.RecordedBytes);
        Assert.Null(item.ObservedFileBytes); Assert.Equal(1, page.UnknownCandidateFileCount); Assert.Equal(0, page.ObservedCandidateBytes);
        RetentionRuntimeOutputTests.NoProbes(f); Assert.Equal(before, UnifiedHistoryProfileFilterTests.RawSnapshot(f.Db.Store.DatabasePath));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(page));
    }

    [Theory]
    [InlineData("future")][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public void CrossSourceSameStringIdIsNotExcludedAndForeignBusinessFieldsAreNotProjected(string source)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate"); f.Db.Ordinary.Read(row.RecordingId);
        PrivateForeign(f.Db, row.PublicationReceipt!.Anchor, source, row.RecordingId);
        Assert.Equal("shared_publication_identity", Assert.Single(f.Page().Items).ReasonCode); RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Theory]
    [InlineData("self")][InlineData("volume")][InlineData("low")][InlineData("high")]
    public void OnlyExactWholeTupleOtherThanBinarySelfCanProtect(string difference)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("same"); var id = row.PublicationReceipt!.Anchor.Identity;
        if (difference != "self") ForeignOrdinary(f, row, identity: difference switch
        { "volume" => id with { VolumeSerialNumber = id.VolumeSerialNumber ^ 1 }, "low" => id with { IdLow = id.IdLow ^ 1 }, _ => id with { IdHigh = id.IdHigh ^ 1 } });
        Assert.Equal("age_rule_match", Assert.Single(f.Page().Items).ReasonCode); Assert.Single(f.Files.Observations);
    }

    [Fact]
    public void CaseDifferentOrdinaryIdIsOtherObjectOwnerAndSharedPathReasonRemainsFirst()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("case");
        ForeignOrdinary(f, row, "CASE"); Assert.Equal("shared_publication_identity", Assert.Single(f.Page().Items).ReasonCode);
        ForeignOrdinary(f, row, "SECRET_PATH", new(1, 2, 3), samePath: true);
        Assert.Equal("shared_output_path", Assert.Single(f.Page().Items).ReasonCode); RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Theory]
    [InlineData("ordinary")][InlineData("future")][InlineData("standing")][InlineData("recurring")][InlineData("required_once")]
    public void MatchedBadBusinessEvidenceCannotBecomeNegative(string source)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate"); f.Db.Ordinary.Read(row.RecordingId);
        if (source == "ordinary")
        {
            ForeignOrdinary(f, row);
            f.Db.Corrupt("UPDATE ordinary_recording_history SET status_code='BROKEN_STATUS' WHERE recording_id='SECRET_OTHER'; UPDATE ordinary_publication_anchors SET source='BROKEN_BUSINESS' WHERE recording_id='SECRET_OTHER';");
        }
        else PrivateForeign(f.Db, row.PublicationReceipt!.Anchor, source, "SECRET_BAD", malformed: true);
        Assert.Equal("shared_publication_identity", Assert.Single(f.Page().Items).ReasonCode); RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Theory]
    [InlineData("future")][InlineData("standing")]
    public void UnmatchedMalformedKeyIsNotClaimedAsComprehensivelyValidated(string source)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate"); f.Db.Ordinary.Read(row.RecordingId);
        PrivateForeign(f.Db, row.PublicationReceipt!.Anchor, source, "SECRET_BAD_KEY", malformed: true, unknownKey: true);
        Assert.Equal("age_rule_match", Assert.Single(f.Page().Items).ReasonCode);
    }

    [Fact]
    public void ValidForeignFutureUsesAnotherSidSessionAndDateWithoutAuthorizedProjection()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate");
        var path = Path.Combine(f.Videos, "SECRET_FUTURE.mp4");
        var scope = f.Db.SeedFuture("candidate", f.Before.AddDays(1), "SECRET_SID", "SECRET_SESSION", path);
        var start = f.Db.StartFuture(scope);
        Assert.True(f.Db.Future.CompleteRun(scope.AuthorizationId, start.RunId, true, "", path, 999, 1000,
            scope.CreatedAtUtc.AddSeconds(3), PublicationReceipt.IssueForTests(path, 999, row.PublicationReceipt!.Anchor.Identity), start));
        Assert.Equal("shared_publication_identity", Assert.Single(f.Page().Items).ReasonCode); RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Fact]
    public void HundredCandidatesUseOneBlobBatchAndAllThreeCoveringIndexSearchesInOriginalTransaction()
    {
        using var f = new RetentionPreviewQueryTests.Fixture();
        for (var i = 0; i < 101; i++) f.Seed("candidate" + i.ToString("D3"));
        var calls = 0; SqliteConnection? connection = null; SqliteTransaction? transaction = null;
        f.Db.Query.RetentionIdentityCommandForTests = (c, t, sql, args) =>
        {
            calls++; connection = c; transaction = t; Assert.Same(c, t.Connection);
            Assert.Equal(300, args.Count); Assert.DoesNotContain("output_path", sql); Assert.DoesNotContain("size", sql);
            Assert.DoesNotContain("JOIN", sql); Assert.DoesNotContain("owner", sql); Assert.DoesNotContain("COUNT", sql);
            Assert.All(args.Where(a => a.Key.StartsWith("$volume")), a => Assert.Equal(8, Assert.IsType<byte[]>(a.Value).Length));
            Assert.All(args.Where(a => a.Key.StartsWith("$file")), a => Assert.Equal(16, Assert.IsType<byte[]>(a.Value).Length));
            var expected = PublicationAnchorTestFixture.SyntheticObject(Path.Combine(f.Videos, (string)args[0].Value + ".mp4"));
            Assert.Equal(expected.VolumeSerialNumber, BinaryPrimitives.ReadUInt64LittleEndian((byte[])args[1].Value));
            Assert.Equal(expected.IdLow, BinaryPrimitives.ReadUInt64LittleEndian((byte[])args[2].Value));
            Assert.Equal(expected.IdHigh, BinaryPrimitives.ReadUInt64LittleEndian(((byte[])args[2].Value).AsSpan(8)));
            using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Key, arg.Value);
            cmd.CommandText = "SELECT typeof($volume0),length($volume0),typeof($file0),length($file0);";
            using (var bound = cmd.ExecuteReader())
            { Assert.True(bound.Read()); Assert.Equal("blob", bound.GetString(0)); Assert.Equal(8, bound.GetInt64(1)); Assert.Equal("blob", bound.GetString(2)); Assert.Equal(16, bound.GetInt64(3)); }
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            var plan = new List<string>(); using (var rd = cmd.ExecuteReader()) while (rd.Read()) plan.Add(rd.GetString(3));
            foreach (var index in SqliteSchemaV42.Indexes)
                Assert.Contains(plan, p => p.Contains("SEARCH a USING COVERING INDEX " + index.Name) && p.Contains("volume_id=? AND file_id=?"));
            Assert.DoesNotContain(plan, p => p.Contains("SCAN a") || p.Contains("MATERIALIZE"));
            output.WriteLine(sql); output.WriteLine(string.Join("\n", plan)); output.WriteLine("BLOB 8/16 LE; candidates=100; parameters=300; same connection/transaction");
        };
        f.Db.Query.RetentionPathCommandForTests = (c, t, _, _) => { Assert.Same(connection, c); Assert.Same(transaction, t); };
        var page = f.Page(100); Assert.Equal(1, calls); Assert.Equal(100, page.Items.Count); Assert.NotNull(page.NextCursor);
        Assert.All(page.Items, p => Assert.Equal("age_rule_match", p.ReasonCode)); Assert.Equal(100, f.Files.Observations.Count);
        f.Db.Query.RetentionIdentityCommandForTests = null; f.Db.Query.RetentionPathCommandForTests = null;
        Assert.Single(f.Page(100, page.NextCursor).Items);
    }

    [Fact]
    public void MultipleCandidatesSharingOneTupleIndividuallyExcludeSelfOnly()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var a = f.Seed("a");
        var b = f.Db.Recording("b", completed: true) with { OutputPath = Path.Combine(f.Videos, "b.mp4"), BundleRegistration = new(null) };
        f.Db.Ordinary.Register(b with { PublicationReceipt = PublicationReceipt.IssueForTests(b.OutputPath, 12, a.PublicationReceipt!.Anchor.Identity) });
        var calls = 0; f.Db.Query.RetentionIdentityCommandForTests = (_, _, _, args) => { calls++; Assert.Equal(6, args.Count); };
        Assert.All(f.Page().Items, p => Assert.Equal("shared_publication_identity", p.ReasonCode)); Assert.Equal(1, calls); RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Fact]
    public void CursorDoesNotRestrictReferencesInsertedBeyondItsHighWater()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); f.Seed("a"); var b = f.Seed("b");
        var first = f.Page(1); Assert.NotNull(first.NextCursor); ForeignOrdinary(f, b);
        var next = f.Page(1, first.NextCursor); Assert.Equal("shared_publication_identity", Assert.Single(next.Items).ReasonCode);
        Assert.Null(next.NextCursor);
    }

    [Theory]
    [InlineData("null")][InlineData("missing")][InlineData("duplicate")][InlineData("foreign")]
    [InlineData("string_flag")][InlineData("integer_flag")][InlineData("large_flag")][InlineData("null_flag")][InlineData("null_id")][InlineData("throw")]
    public void BadProviderResultsFailCompletePageBeforeRuntimeOrAnyFilesystem(string fault)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); f.Seed("a"); f.Seed("b");
        f.Db.Query.RetentionIdentityResultsForTests = rows => fault switch
        {
            "null" => null!, "missing" => rows.Take(1).ToArray(), "duplicate" => new[] { rows[0], rows[0] },
            "foreign" => new[] { rows[0], ((object)"SECRET_FOREIGN", (object)0L) },
            "string_flag" => new[] { rows[0], (rows[1].CandidateId, (object)"0") },
            "integer_flag" => new[] { rows[0], (rows[1].CandidateId, (object)0) },
            "large_flag" => new[] { rows[0], (rows[1].CandidateId, (object)2L) },
            "null_flag" => new[] { rows[0], (rows[1].CandidateId, (object)DBNull.Value) },
            "null_id" => new[] { rows[0], ((object)DBNull.Value, (object)0L) },
            _ => throw new IOException("SECRET_PROVIDER")
        };
        var provider = new RetentionRuntimeOutputTests.Provider();
        var service = new RetentionPreviewQueryService(f.Db.Query.GetOrdinaryEvidencePage, f.Db.Root, f.Files, f.Files, () => At.AddDays(2), provider);
        Assert.Throws<RetentionPreviewUnavailableException>(() => service.GetPage(Sid, Session, 20, f.Before));
        Assert.Empty(provider.Calls); RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Theory]
    [InlineData("missing")][InlineData("invalid")][InlineData("unanchored_checked")]
    public void UncheckedOrInvalidFactCannotHideBehindEarlierProtection(string fault)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); f.Seed("a"); f.Seed("b");
        var page = f.Db.Query.GetOrdinaryEvidencePage(Sid, Session, 20, f.Before, null);
        var bad = page.Items[1] with { NestedReference = OrdinaryNestedReferenceState.Relationship,
            PublicationIdentityReference = fault == "invalid" ? (PublicationIdentityReferenceState)999 : fault == "missing" ? null : PublicationIdentityReferenceState.None,
            PublicationAnchor = fault == "unanchored_checked" ? null : page.Items[1].PublicationAnchor };
        var provider = new RetentionRuntimeOutputTests.Provider();
        var service = new RetentionPreviewQueryService((_, _, _, _, _) => page with { Items = new[] { page.Items[0], bad } }, f.Db.Root, f.Files, f.Files, () => At.AddDays(2), provider);
        Assert.Throws<RetentionPreviewUnavailableException>(() => service.GetPage(Sid, Session, 20, f.Before));
        Assert.Empty(provider.Calls); RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Theory]
    [InlineData("bundle")][InlineData("identity")][InlineData("runtime")][InlineData("missing")]
    public void PriorityAndUnknownCountersKeepExistingBoundary(string winner)
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate");
        if (winner is "identity" or "bundle") ForeignOrdinary(f, row);
        if (winner == "missing") { f.Db.Ordinary.Read(row.RecordingId); f.Db.Execute("DELETE FROM ordinary_publication_anchors;"); }
        var provider = new RetentionRuntimeOutputTests.Provider();
        var service = new RetentionPreviewQueryService((sid, session, limit, before, cursor) =>
        {
            var page = f.Db.Query.GetOrdinaryEvidencePage(sid, session, limit, before, cursor);
            return winner != "bundle" ? page : page with { Items = page.Items.Select(e => e with
                { Entry = e.Entry with { Bundle = new("generating", Path.ChangeExtension(row.OutputPath, "bundle"), null) } }).ToArray() };
        }, f.Db.Root, f.Files, f.Files, () => At.AddDays(2), provider);
        var page = service.GetPage(Sid, Session, 20, f.Before);
        Assert.Equal(winner switch { "bundle" => "bundle_generation_in_progress", "identity" => "shared_publication_identity", "runtime" => "runtime_output_in_use", _ => "publication_identity_missing" }, Assert.Single(page.Items).ReasonCode);
        Assert.Equal(1, page.AgeCandidateCount); Assert.Equal(1, page.UnknownCandidateFileCount); Assert.Equal(0, page.ObservedCandidateBytes);
        RetentionRuntimeOutputTests.NoProbes(f);
    }

    [Fact]
    public void WalReferenceInsertedAfterSnapshotIsOnlyVisibleToNextRead()
    {
        using var f = new RetentionPreviewQueryTests.Fixture(); var row = f.Seed("candidate"); var calls = 0;
        f.Db.Query.RetentionIdentityCommandForTests = (_, _, _, _) =>
        { calls++; PrivateForeign(f.Db, row.PublicationReceipt!.Anchor, "future", "SECRET_LATER"); };
        Assert.Equal("age_rule_match", Assert.Single(f.Page().Items).ReasonCode); Assert.Equal(1, calls);
        f.Db.Query.RetentionIdentityCommandForTests = null;
        Assert.Equal("shared_publication_identity", Assert.Single(f.Page().Items).ReasonCode);
    }
}
