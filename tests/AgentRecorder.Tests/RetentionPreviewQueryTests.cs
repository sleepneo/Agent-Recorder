using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class RetentionPreviewQueryTests
{
    internal sealed class Fixture : IDisposable
    {
        internal UnifiedHistoryTestDatabase Db { get; } = new(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task325r", Guid.NewGuid().ToString("N")));
        internal DateTimeOffset Before = At.AddHours(1);
        internal string Videos => Path.Combine(Db.Root, "Videos");
        internal MetadataFake Files = new();
        internal int Reads, OldProbes, Selected, Validated;
        private readonly bool _realFiles;
        internal RetentionPreviewQueryService Service { get; }
        internal Fixture(bool realFiles = false, bool ordinaryOnly = false)
        {
            _realFiles = realFiles;
            var query = ordinaryOnly ? new UnifiedHistoryQueryService(Db.Store, Db.Ordinary, true) : Db.Query;
            query.OrdinaryFileProbeForTests = _ => { OldProbes++; return true; };
            query.CandidatesSelectedForTests = (s, c) => { Assert.Equal("ordinary", s); Selected += c; };
            query.EntriesValidatedForTests = (s, c) => { Assert.Equal("ordinary", s); Validated += c; };
            Service = new((sid, session, limit, cutoff, cursor) => { Reads++; return query.GetOrdinaryEvidencePage(sid, session, limit, cutoff, cursor); },
                Db.Root, realFiles ? ReadOnlyPathMetadata.Instance : Files, realFiles ? WindowsReadOnlyFileMetadataProbe.Instance : Files, () => At.AddDays(2), TestRuntimeOutputUse.None);
        }
        internal OrdinaryRecordingHistorySnapshot Seed(string id, string status = "completed", DateTimeOffset? completed = null, string? path = null)
        {
            var row = Db.Recording(id, completed: true) with { Status = status, OutputPath = path ?? Path.Combine(Videos, id + ".mp4"),
                CompletedAtUtc = completed ?? At.AddSeconds(16) };
            if (status != "completed") row = row with { StartedAtUtc = null, CompletedAtUtc = null, OutputSizeBytes = null, OutputDurationSeconds = null };
            row = PublicationAnchorTestFixture.Anchored(row, _realFiles);
            if (row.Status == "completed") row = row with { BundleRegistration = new(null) }; Db.Ordinary.Register(row); return row;
        }
        internal RetentionPreviewPage Page(int limit = 20, string? cursor = null, DateTimeOffset? before = null, string sid = Sid, string session = Session)
            => Service.GetPage(sid, session, limit, before ?? Before, cursor);
        public void Dispose() => Db.Dispose();
    }
    internal sealed class MetadataFake : IReadOnlyPathMetadata, IReadOnlyFileMetadataProbe
    {
        internal List<string> Attributes = new(), Lengths = new();
        internal Func<string, FileAttributes>? Attribute;
        internal Func<string, long>? Length;
        internal long DefaultLength = 12;
        public FileAttributes GetAttributes(string path) { Attributes.Add(path); return Attribute?.Invoke(path) ?? (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? FileAttributes.Normal : FileAttributes.Directory); }
        // Legacy counter name retained: records one synthetic handle length sample,
        // never a path-length call. Every fake supplies explicit stable identity.
        internal List<string> Observations = new(), ProbeAttributes = new();
        internal Func<string, FileMetadataObservation?>? Observation;
        public long GetLength(string path) => throw new InvalidOperationException("Forbidden path length fallback.");
        public FileMetadataObservation? Observe(string path)
        {
            Observations.Add(path);if(Observation is not null)return Observation(path);
            ProbeAttributes.Add(path);var first=Attribute?.Invoke(path)??FileAttributes.Normal;
            if((first&FileAttributes.ReparsePoint)!=0)return new(FileObservationOutcome.ReparsePoint);
            if((first&FileAttributes.Directory)!=0)return new(FileObservationOutcome.Directory);
            Lengths.Add(path);var length=Length?.Invoke(path)??DefaultLength;
            ProbeAttributes.Add(path);var second=Attribute?.Invoke(path)??FileAttributes.Normal;
            if((second&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)return new(FileObservationOutcome.MetadataChanged);
            var a=new HandleMetadataSnapshot(length,0,1,first,0,0,133000000000000000,PublicationAnchorTestFixture.SyntheticObject(path));
            return new(FileObservationOutcome.Sampled,a,a with{Attributes=second},path,path);
        }
    }

    [Fact]
    public void EmptyIsOneReadAndNoFileProbesOrBusinessWrites()
    {
        using var f = new Fixture(); var snapshot = f.Db.Snapshot(); var page = f.Page();
        Assert.Empty(page.Items); Assert.Null(page.NextCursor); Assert.Equal(0, page.EvaluatedCount); Assert.Equal(0, page.AgeCandidateCount);
        Assert.Equal(1, f.Reads); Assert.Equal(0, f.OldProbes); Assert.Empty(f.Files.Attributes); Assert.Equal(snapshot, f.Db.Snapshot());
    }

    [Fact]
    public void CompletionTicksNotCreationOrFileDatesDefineAgeAndAllSkippedPageStillHasNext()
    {
        using var f = new Fixture();
        f.Seed("a", completed: f.Before.AddTicks(1)); f.Seed("b", completed: f.Before); f.Seed("c", completed: f.Before.AddTicks(-1));
        var first = f.Page(2); Assert.Equal(2, first.EvaluatedCount); Assert.Equal(0, first.AgeCandidateCount); Assert.NotNull(first.NextCursor);
        Assert.All(first.Items, e => Assert.Equal("completion_not_before_cutoff", e.ReasonCode)); Assert.Empty(f.Files.Attributes);
        var next = f.Page(1, first.NextCursor); Assert.Equal("ordinary:c", Assert.Single(next.Items).EntryId); Assert.Equal(1, next.AgeCandidateCount);
        Assert.Null(next.NextCursor); Assert.Equal(12, next.ObservedCandidateBytes); Assert.Equal(0, f.OldProbes);
    }

    [Theory]
    [InlineData("pending_confirmation")] [InlineData("preparing")] [InlineData("countdown")] [InlineData("recording")]
    [InlineData("finalizing")] [InlineData("cancelled")] [InlineData("rejected")] [InlineData("failed")]
    public void NonCompletedAndUnapprovedNeverProbeOrQualify(string status)
    {
        using var f = new Fixture(); f.Seed("active", status); var entry = Assert.Single(f.Page().Items);
        Assert.Equal("not_completed", entry.ReasonCode); Assert.False(entry.AgeCandidate); Assert.Null(entry.RecordedPath); Assert.Empty(f.Files.Attributes); Assert.Equal(0, f.OldProbes);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void MissingCompletionAndMissingEvidenceRemainProtected(bool missingCompletion)
    {
        using var f = new Fixture(); var row = f.Db.Recording("legacy", completed: true) with { OutputPath = Path.Combine(f.Videos, "legacy.mp4") };
        row = missingCompletion ? row with { CompletedAtUtc = null } : row with { OutputSizeBytes = 0 };
        f.Db.Ordinary.Register(row); var entry = Assert.Single(f.Page().Items);
        Assert.Equal(missingCompletion ? "completion_time_unknown" : "output_evidence_missing", entry.ReasonCode); Assert.Empty(f.Files.Attributes); Assert.Equal(0, f.OldProbes);
    }

    [Theory]
    [InlineData("external")] [InlineData("prefix")] [InlineData("extension")] [InlineData("device")] [InlineData("device_prefix")] [InlineData("unc")] [InlineData("depth")] [InlineData("length")]
    public void ScopeAndPathBoundsRejectBeforeAnyProbeIncludingDefaultHistoryProjection(string kind)
    {
        using var f = new Fixture();
        var path = kind switch
        {
            "external" => Path.Combine(f.Db.Root, "Other", "out.mp4"), "prefix" => Path.Combine(f.Db.Root, "Videos-old", "out.mp4"),
            "extension" => Path.Combine(f.Videos, "out.png"), "device" => Path.Combine(f.Videos, "CON.mp4"),
            "device_prefix" => @"\\?\" + Path.Combine(f.Videos,"out.mp4"), "unc" => @"\\server\share\out.mp4",
            "depth" => Path.Combine(f.Videos, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("deep", 33)), "out.mp4"),
            _ => Path.Combine(f.Videos, string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat(new string('x', 150), 8)), "out.mp4")
        };
        f.Seed("scoped", path: path); var entry = Assert.Single(f.Page().Items);
        Assert.Equal("protected", entry.Evaluation); Assert.False(entry.AgeCandidate); Assert.Null(entry.RecordedPath); Assert.Null(entry.RecordedBytes);
        Assert.Empty(f.Files.Attributes); Assert.Empty(f.Files.Lengths); Assert.Equal(0, f.OldProbes);
    }

    [Theory]
    [InlineData("file")] [InlineData("root")] [InlineData("parent")] [InlineData("ancestor")]
    public void EveryReparseLevelStopsBeforeLengthOrFollowingLinkedChild(string level)
    {
        using var f = new Fixture(); var path = Path.Combine(f.Videos, "child", "out.mp4");
        var link = level switch { "file" => path, "root" => f.Videos, "parent" => Path.GetDirectoryName(path)!, _ => f.Db.Root };
        f.Files.Attribute = p => p == link ? FileAttributes.ReparsePoint | FileAttributes.Directory : p == path ? FileAttributes.Normal : FileAttributes.Directory;
        f.Seed("link", path: path); var entry = Assert.Single(f.Page().Items);
        Assert.Equal("reparse_point", entry.ReasonCode); Assert.Equal(link, level=="file" ? f.Files.ProbeAttributes.Last() : f.Files.Attributes.Last()); Assert.Empty(f.Files.Lengths); Assert.Null(entry.ObservedFileBytes);
        Assert.Equal(1, f.Page().UnknownCandidateFileCount);
    }

    [Theory]
    [InlineData("missing", "file_missing")] [InlineData("parent", "parent_missing")] [InlineData("permission", "file_inaccessible")]
    [InlineData("unknown", "metadata_unavailable")] [InlineData("directory", "output_not_file")] [InlineData("changed", "file_length_changed")]
    [InlineData("negative", "metadata_changed")] [InlineData("race", "metadata_changed")]
    [InlineData("length_permission", "file_inaccessible")] [InlineData("length_io", "metadata_unavailable")]
    public void MetadataFailuresAreWhitelistedAndReliableChangedLengthIsKeptSeparate(string failure, string expected)
    {
        using var f = new Fixture(); var row = f.Seed("file"); var fileAttributes = 0;
        f.Files.Attribute = p =>
        {
            if (failure == "parent" && p == f.Videos) throw new DirectoryNotFoundException("private-parent");
            if (p != row.OutputPath) return FileAttributes.Directory;
            fileAttributes++;
            return failure switch
            {
                "missing" => throw new FileNotFoundException("private-file"), "permission" => throw new UnauthorizedAccessException("private-permission"),
                "unknown" => throw new IOException("private-io"), "directory" => FileAttributes.Directory,
                "race" when fileAttributes == 2 => FileAttributes.ReparsePoint, _ => FileAttributes.Normal
            };
        };
        f.Files.DefaultLength = failure == "changed" ? 13 : failure == "negative" ? -1 : 12;
        f.Files.Length = _ => failure switch { "length_permission" => throw new UnauthorizedAccessException("private-length"), "length_io" => throw new IOException("private-length"), _ => f.Files.DefaultLength };
        var page = f.Page(); var entry = Assert.Single(page.Items); Assert.Equal(expected, entry.ReasonCode);
        Assert.Equal("protected", entry.Evaluation); Assert.True(entry.AgeCandidate); Assert.Equal(12, entry.RecordedBytes);
        Assert.DoesNotContain("private", entry.ReasonCode);
        if (failure == "changed") { Assert.Equal(13, entry.ObservedFileBytes); Assert.Equal(13, page.ObservedCandidateBytes); Assert.Equal(0, page.UnknownCandidateFileCount); }
        else { Assert.Null(entry.ObservedFileBytes); Assert.Equal(1, page.UnknownCandidateFileCount); }
    }

    [Fact]
    public void PhysicalMetadataOnlyDoesNotUseCreationOrLastWriteDatesOrModifyMedia()
    {
        using var f = new Fixture(realFiles: true); Directory.CreateDirectory(f.Videos); var bytes = new byte[12]; var path = Path.Combine(f.Videos,"real.mp4"); File.WriteAllBytes(path,bytes); var row = f.Seed("real");
        File.SetLastWriteTimeUtc(row.OutputPath, At.AddYears(1).UtcDateTime); var write = File.GetLastWriteTimeUtc(row.OutputPath); var snapshot = f.Db.Snapshot();
        var entry = Assert.Single(f.Page().Items); Assert.Equal("age_candidate", entry.Evaluation); Assert.Equal(12, entry.ObservedFileBytes);
        Assert.Equal(bytes, File.ReadAllBytes(row.OutputPath)); Assert.Equal(write, File.GetLastWriteTimeUtc(row.OutputPath)); Assert.Equal(snapshot, f.Db.Snapshot()); Assert.Equal(0, f.OldProbes);
        File.Delete(row.OutputPath); Assert.Equal("file_missing", Assert.Single(f.Page().Items).ReasonCode);
    }

    [Fact]
    public void CursorBindsCutoffOwnerAndMembershipWhileStatesAndFilesStayLive()
    {
        using var f = new Fixture(); f.Seed("a"); f.Seed("b"); f.Seed("c"); var first = f.Page(1); Assert.NotNull(first.NextCursor);
        Assert.Throws<UnifiedHistoryArgumentException>(() => f.Page(1, first.NextCursor, f.Before.AddTicks(1)));
        Assert.Throws<UnifiedHistoryArgumentException>(() => f.Page(1, first.NextCursor, sid: "different-owner"));
        Assert.Throws<UnifiedHistoryArgumentException>(() => f.Page(1, first.NextCursor, session: "different-session"));
        Assert.Throws<UnifiedHistoryArgumentException>(() => f.Page(1, "bad")); Assert.Throws<UnifiedHistoryArgumentException>(() => f.Page(1, ""));
        var headless = new RetentionPreviewQueryService(new UnifiedHistoryQueryService(f.Db.Store,f.Db.Ordinary,true),f.Db.Root, TestRuntimeOutputUse.None);
        Assert.Throws<UnifiedHistoryArgumentException>(() => headless.GetPage(Sid,Session,20,f.Before,first.NextCursor));
        f.Seed("bb"); f.Files.DefaultLength = 25; var next = f.Page(100, first.NextCursor);
        Assert.Equal(new[] { "ordinary:b", "ordinary:c" }, next.Items.Select(e => e.EntryId)); Assert.All(next.Items, e => Assert.Equal("file_length_changed", e.ReasonCode));
        Assert.Equal(50, next.ObservedCandidateBytes); Assert.Null(next.NextCursor);
        f.Db.Execute("UPDATE ordinary_recording_history SET status_code='failed' WHERE recording_id='b';");
        Assert.Throws<RetentionPreviewUnavailableException>(()=>f.Page(100,first.NextCursor)); // anchored terminal association damage
        f.Db.Execute("DELETE FROM ordinary_publication_anchors WHERE recording_id='b';"); // restore a legacy unanchored status fixture only
        var live=f.Page(100,first.NextCursor); Assert.Equal("not_completed",live.Items[0].ReasonCode); Assert.Equal(1,live.AgeCandidateCount); Assert.Equal(25,live.ObservedCandidateBytes);
    }

    [Fact]
    public void EntireEvidenceFailurePrecedesFileSamplesAndWriterBarrierFailureIsUnavailable()
    {
        using (var f = new Fixture())
        {
            f.Seed("a"); f.Seed("b"); f.Db.Corrupt("UPDATE ordinary_recording_history SET completed_at_utc=-1 WHERE recording_id='b';");
            var snapshot = f.Db.Snapshot(); Assert.Throws<RetentionPreviewUnavailableException>(() => f.Page()); Assert.Empty(f.Files.Attributes); Assert.Equal(snapshot, f.Db.Snapshot());
        }
        using (var f = new Fixture())
        {
            var row = f.Seed("pending", "preparing"); f.Db.Ordinary.BeforePersistForTests = _ => throw new IOException("private-write");
            Assert.True(f.Db.Ordinary.TryPublish(row with { Status = "failed", CompletedAtUtc = At.AddMinutes(1) }));
            Assert.Throws<RetentionPreviewUnavailableException>(() => f.Page()); Assert.Empty(f.Files.Attributes); Assert.Equal(0, f.OldProbes);
        }
    }

    [Fact]
    public void LargeStoreUsesOneIndexedBoundedReadAndFiniteChecksWithoutFillingProtectedPage()
    {
        using var f = new Fixture();
        for (var i = 0; i < 1000; i++) f.Seed("r" + i.ToString("D4"));
        string? sql = null; f.Db.Query.CandidateCommandForTests = (_, command, args) => { sql = command; Assert.Contains(args, p => p.Key == "$take" && Convert.ToInt32(p.Value) == 21); };
        var snapshot = f.Db.Snapshot(); var page = f.Page(); Assert.Equal(20, page.EvaluatedCount); Assert.Equal(20, page.AgeCandidateCount); Assert.NotNull(page.NextCursor);
        Assert.Equal(1, f.Reads); Assert.Equal(21, f.Selected); Assert.Equal(20, f.Validated); Assert.Equal(0, f.OldProbes);
        Assert.True(f.Files.Attributes.Count <= 20 * RetentionPreviewQueryService.MaxAttributeChecksPerEntry); Assert.Equal(20, f.Files.Lengths.Count);
        using (var c = f.Db.Store.OpenReadOnlyConnection()) { using var cmd = c.CreateCommand(); cmd.CommandText = "EXPLAIN QUERY PLAN " + sql; cmd.Parameters.AddWithValue("$watermark",1000); cmd.Parameters.AddWithValue("$before",f.Before.UtcTicks); cmd.Parameters.AddWithValue("$take",21); using var r = cmd.ExecuteReader(); var plan = new List<string>(); while (r.Read()) plan.Add(r.GetString(3)); Assert.Contains(plan, s => s.Contains("SEARCH ordinary_recording_history USING INDEX idx_ordinary_recording_history_created")); }
        Assert.Equal(snapshot, f.Db.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SamePreviewCoreInBothHostScopesExcludesPlanFutureAndIgnoresOutputSettings(bool headless)
    {
        using var f = new Fixture(ordinaryOnly: headless); f.Seed("ordinary"); f.Db.SeedPlan("plan"); f.Db.SeedOccurrence("plan", "occurrence"); f.Db.SeedFuture("future");
        Directory.CreateDirectory(Path.Combine(f.Db.Root, "config")); File.WriteAllText(Path.Combine(f.Db.Root, "config", "output-settings.json"), "{\"default_output_directory\":\"C:\\Elsewhere\"}");
        Assert.Equal(f.Videos, f.Service.VideosRootForTests); Assert.Equal("ordinary:ordinary", Assert.Single(f.Page().Items).EntryId); Assert.Equal(1, f.Reads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CheckedAggregateAllowsLongMaxAndFailsWholePageOnOverflow(bool overflow)
    {
        var root = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task325r/synthetic")); var files = new MetadataFake { DefaultLength = long.MaxValue };
        var entries = Enumerable.Range(0, overflow ? 2 : 1).Select(i => new UnifiedHistoryEntry("ordinary:"+i,"ordinary","ordinary_video",At,RecordingId:i.ToString(),Status:"completed",OutputPath:Path.Combine(root,"Videos",i+".mp4"),OutputPathRecorded:true,OutputSizeBytes:long.MaxValue,CompletedAtUtc:At.AddSeconds(1),Bundle:new("not_applicable",null,null))).ToArray();
        var service = new RetentionPreviewQueryService((_,_,limit,before,_) => new(entries.Select(e=>new OrdinaryRetentionEvidence(e,OrdinaryNestedReferenceState.None,SharedOutputPathReferenceState.None,PlannedOutputPathReferenceState.None) { PublicationAnchor = new(e.OutputPath!,e.OutputSizeBytes!.Value,PublicationAnchorTestFixture.SyntheticObject(e.OutputPath!)), PublicationIdentityReference = PublicationIdentityReferenceState.None }).ToArray(),null,limit,new("ordinary",CreatedBefore:before)), root, files, files, () => At.AddDays(2), TestRuntimeOutputUse.None);
        if (overflow) Assert.Throws<RetentionPreviewUnavailableException>(() => service.GetPage(Sid,Session,20,At.AddHours(1)));
        else Assert.Equal(long.MaxValue, service.GetPage(Sid,Session,20,At.AddHours(1)).ObservedCandidateBytes);
    }

    [Fact]
    public void WindowsCaseInsensitiveScopeAndExactDepthBoundAllowFiniteSample()
    {
        using var f=new Fixture();
        var components=f.Videos[Path.GetPathRoot(f.Videos)!.Length..].Split(Path.DirectorySeparatorChar).Length;
        var path=Path.Combine(f.Videos,string.Join(Path.DirectorySeparatorChar,Enumerable.Repeat("d",RetentionPreviewQueryService.MaxPathComponents-components-1)),"out.MP4").ToUpperInvariant();
        f.Seed("case",path:path); Assert.Equal("age_candidate",Assert.Single(f.Page().Items).Evaluation);
        Assert.Equal(RetentionPreviewQueryService.MaxAttributeChecksPerEntry,f.Files.Attributes.Count); Assert.Single(f.Files.Lengths);
    }

    [Fact]
    public void PublicHistoryStillProbesAndFutureOrNonUtcCutoffsAreRejectedBeforeRead()
    {
        using var f = new Fixture(); f.Seed("public", path: Path.Combine(f.Db.Root,"External","public.mp4"));
        Assert.True(Assert.Single(f.Db.Page(filters: new("ordinary")).Items).OutputFileExists); Assert.Equal(1,f.OldProbes);
        Assert.Throws<UnifiedHistoryArgumentException>(() => f.Page(before:At.AddDays(3)));
        Assert.Throws<UnifiedHistoryArgumentException>(() => f.Page(before:f.Before.ToOffset(TimeSpan.FromHours(8)))); Assert.Equal(0,f.Reads);
    }
}
