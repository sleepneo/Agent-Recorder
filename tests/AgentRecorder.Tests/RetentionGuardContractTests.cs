using System.Reflection;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;
using static AgentRecorder.Tests.RetentionGuardFixture;

namespace AgentRecorder.Tests;

public sealed class RetentionGuardContractTests
{
    [Theory]
    [InlineData("status_code", "'failed'")][InlineData("completed_at_utc", "NULL")]
    [InlineData("output_size_bytes", "0")][InlineData("output_duration_ms", "0")]
    [InlineData("output_size_bytes", "13")]
    public async Task CompletedSuccessEvidenceIsRequired(string column, string expression)
    {
        using var f = new RetentionGuardFixture(); f.Db.Corrupt($"UPDATE ordinary_recording_history SET {column}={expression};");
        using var g = f.Guard(); var calls = 0;
        Assert.Equal(RetentionGuardReason.CandidateRejected, await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExactSingleCandidateNoFilesNoBusinessWritesContextExpires()
    {
        using var f = new RetentionGuardFixture(); var before = SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath, true);
        var adapter = new TraceDatabase(); using var g = f.Guard(adapter);
        RetentionReferenceGuard.ScopeContext? context = null; var calls = 0;
        Assert.Equal(RetentionGuardReason.Checked, await Finish(f.Execute(g, scope =>
        { calls++; context = scope; scope.RequireActive(); Assert.Equal(f.Row.RecordingId, scope.RecordingId); return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(1, calls); Assert.Throws<InvalidOperationException>(() => context!.RequireActive());
        Assert.All(typeof(RetentionReferenceGuard.ScopeContext).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), c => Assert.True(c.IsPrivate));
        Assert.Equal(before, SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath, true));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.Row.OutputPath))); Assert.Equal(44, SqliteOperationalStore.CurrentSchemaVersion);
        Assert.Single(adapter.Threads.Distinct()); Assert.Equal(1, adapter.Begins); Assert.Equal(1, adapter.Rollbacks);
        Assert.Equal(1, adapter.Closes); Assert.Equal(1, adapter.ConnectionDisposes); Assert.Equal(1, adapter.TransactionDisposes);
        Assert.Equal(RetentionGuardReason.Checked, await Finish(f.Execute(g))); // True retirement releases admission.
    }

    [Theory]
    [InlineData("id")][InlineData("case_id")][InlineData("sid")][InlineData("binding")][InlineData("anchor_length")]
    [InlineData("anchor_volume")][InlineData("anchor_low")][InlineData("anchor_high")][InlineData("anchor_path")]
    [InlineData("cutoff_equal")][InlineData("cutoff_non_utc")][InlineData("cutoff_future")][InlineData("anchor_invalid")]
    public async Task CandidateAndHostInputsFailClosed(string kind)
    {
        using var f = new RetentionGuardFixture("CaseID");
        using var g = f.Guard(sid: kind == "sid" ? "bad\0identity" : UnifiedHistoryTestDatabase.Sid,
            binding: kind == "binding" ? " " : UnifiedHistoryTestDatabase.Session);
        var a = kind switch
        {
            "anchor_length" => f.Anchor with { Length = 13 },
            "anchor_volume" => f.Anchor with { Identity = f.Anchor.Identity with { VolumeSerialNumber = 1 } },
            "anchor_low" => f.Anchor with { Identity = f.Anchor.Identity with { IdLow = 1 } },
            "anchor_high" => f.Anchor with { Identity = f.Anchor.Identity with { IdHigh = 1 } },
            "anchor_path" => f.Anchor with { Path = f.Anchor.Path.ToUpperInvariant() },
            "anchor_invalid" => f.Anchor with { Length = 0 }, _ => f.Anchor
        };
        var cutoff = kind switch { "cutoff_equal" => f.Row.CompletedAtUtc!.Value, "cutoff_non_utc" => f.Cutoff.ToOffset(TimeSpan.FromHours(8)), "cutoff_future" => DateTimeOffset.UtcNow.AddDays(1), _ => f.Cutoff };
        var calls = 0; var result = await Finish(g.Execute(kind == "id" ? "absent" : kind == "case_id" ? "caseid" : f.Row.RecordingId, a, cutoff, _ => { calls++; return RetentionGuardContinuationResult.Completed; }));
        Assert.Contains(result, new[] { RetentionGuardReason.InvalidInput, RetentionGuardReason.CandidateRejected }); Assert.Equal(0, calls);
    }

    public static IEnumerable<object[]> Receipts => Enumerable.Range(1, 44).Select(v => new object[] { v });
    [Theory, MemberData(nameof(Receipts))]
    public async Task EveryMissingReceiptIsUnavailableAndNeverRepaired(int version)
    {
        using var f = new RetentionGuardFixture(); f.Db.RawExecute($"DELETE FROM schema_migrations WHERE version={version};");
        var before = SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath, true);
        using var g = f.Guard(); var calls = 0;
        Assert.Equal(RetentionGuardReason.Unavailable, await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; })));
        Assert.Equal(0, calls); Assert.Equal(before, SqlitePublicationIdentityReferenceTests.OldBytes(f.Db.Store.DatabasePath, true));
    }
    public static IEnumerable<object[]> Indexes => SqliteSchemaV28.Indexes.Concat(SqliteSchemaV29.Indexes).Concat(SqliteSchemaV38.Indexes).Concat(SqliteSchemaV39.Indexes)
        .Select(i => i.Name).Concat(SqliteSchemaV42.Indexes.Select(i => i.Name)).Append(SqliteSchemaV25.IndexName).Select(i => new object[] { i });
    [Theory, MemberData(nameof(Indexes))]
    public async Task EveryRelationshipIndexRequired(string index)
    {
        using var f = new RetentionGuardFixture(); f.Db.RawExecute("DROP INDEX " + index + ";"); using var g = f.Guard();
        Assert.Equal(RetentionGuardReason.Unavailable, await Finish(f.Execute(g)));
    }
    [Theory]
    [InlineData("ordinary_recording_history", "output_path")][InlineData("ordinary_recording_history", "status_code")]
    [InlineData("ordinary_recording_history", "parent_recording_id")][InlineData("ordinary_recording_bundles", "status_code")]
    [InlineData("ordinary_recording_bundles", "media_output_path")][InlineData("ordinary_publication_anchors", "output_path")]
    [InlineData("schema_migrations", "name")][InlineData("schema_migrations", "definition_checksum")]
    public async Task FullTextNulCannotBeTruncatedIntoValidEvidence(string table, string column)
    {
        using var f = new RetentionGuardFixture(); f.Db.Corrupt($"UPDATE {table} SET {column}=COALESCE({column},'parent')||char(0)||'PRIVATE';");
        using var g = f.Guard(); Assert.Equal(RetentionGuardReason.Unavailable, await Finish(f.Execute(g)));
    }
    [Theory]
    [InlineData("output_size_bytes", "'12'")][InlineData("created_at_utc", "CAST(created_at_utc AS BLOB)")]
    [InlineData("output_path", "CAST(output_path AS BLOB)")][InlineData("status_code", "CAST(status_code AS BLOB)")]
    public async Task DamagedTypedEvidenceCannotCoerceIntoCandidate(string column, string expression)
    {
        using var f = new RetentionGuardFixture(); f.Db.Corrupt($"UPDATE ordinary_recording_history SET {column}={expression};");
        using var g = f.Guard();
        // INTEGER affinity converts '12' on write, so this is the lawful positive control.
        Assert.Equal(column == "output_size_bytes" ? RetentionGuardReason.Checked : RetentionGuardReason.Unavailable, await Finish(f.Execute(g)));
    }
    [Theory]
    [InlineData("missing")][InlineData("generating")][InlineData("ready")][InlineData("failed")][InlineData("interrupted_unknown")]
    public async Task OnlyExplicitNotApplicableBundleCanContinue(string status)
    {
        using var f = new RetentionGuardFixture();
        if (status == "missing") f.Db.Corrupt("DELETE FROM ordinary_recording_bundles;");
        else f.Db.Corrupt("UPDATE ordinary_recording_bundles SET status_code=$status,bundle_path=$path,operation_id=$op,error_code=$error;",
            ("$status", status), ("$path", OrdinaryRecordingBundleOperation.DerivePath(f.Row.OutputPath)),
            ("$op", status == "interrupted_unknown" ? DBNull.Value : new string('a', 32)),
            ("$error", status == "failed" ? "bundle_failed" : status == "interrupted_unknown" ? "bundle_interrupted_unknown" : DBNull.Value));
        using var g = f.Guard(); var calls = 0; var reason = await Finish(f.Execute(g, _ => { calls++; return RetentionGuardContinuationResult.Completed; }));
        Assert.Contains(reason, new[] { RetentionGuardReason.BundleProtected, RetentionGuardReason.Unavailable }); Assert.Equal(0, calls);
    }
    [Fact]
    public async Task ExistingOnlyMissingDatabaseDoesNotCreateParentOrFile()
    {
        using var f = new RetentionGuardFixture(); var missing = Path.Combine(f.Db.Root, "NeverCreated", "missing.db");
        var store = new SqliteOperationalStore(missing); using var writer = new SqliteOrdinaryRecordingHistoryStore(store);
        using var g = new RetentionReferenceGuard(store, writer, UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, new());
        Assert.Equal(RetentionGuardReason.Unavailable, await Finish(g.Execute(f.Row.RecordingId, f.Anchor, f.Cutoff, _ => RetentionGuardContinuationResult.Completed)));
        Assert.False(File.Exists(missing)); Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }
}
