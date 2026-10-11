using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentRecorder.Core;
using AgentRecorder.Persistence;
using AgentRecorder.Infrastructure;
using Xunit;
using static AgentRecorder.Tests.UnifiedHistoryTestDatabase;

namespace AgentRecorder.Tests;

public sealed class UnifiedHistoryStatusFilterTests
{
    public static IEnumerable<object[]> Statuses => OrdinaryRecordingHistoryStatusCodes.Supported.Select(s => new object[] { s });
    [Theory, MemberData(nameof(Statuses))]
    public void EveryPersistentStateIsSelectedBeforeLimitAndNullKeepsOldResults(string status)
    {
        using var d = new UnifiedHistoryTestDatabase();
        foreach (var s in OrdinaryRecordingHistoryStatusCodes.Supported)
            d.Ordinary.Register(d.Recording(s) with { Status = s });
        var before = d.Snapshot();
        var page = d.Page(1, new("ordinary", Status: status));
        Assert.Equal(status, Assert.Single(page.Items).Status); Assert.Equal(status, page.Filters.Status); Assert.Null(page.NextCursor);
        Assert.Equal(11, d.Page(100, new("ordinary")).Items.Count);
        Assert.Equal(before, d.Snapshot());
    }

    [Theory]
    [InlineData(1)] [InlineData(20)] [InlineData(100)]
    public void CombinedCreationBoundsBinaryTiesAndDeepPagesHaveNoDuplicates(int limit)
    {
        using var d = new UnifiedHistoryTestDatabase();
        var ids = Enumerable.Range(0, 125).Select(i => $"r{i:000}").Concat(new[] { "A", "a", "é", "中" }).ToArray();
        foreach (var id in ids) d.Ordinary.Register(d.Recording(id) with { Status = "failed" });
        d.Ordinary.Register(d.Recording("outside-before", At.AddTicks(-1)) with { Status = "failed" });
        d.Ordinary.Register(d.Recording("outside-after", At.AddTicks(1)) with { Status = "failed" });
        d.SeedOrdinary("nonmatching");
        var filters = new UnifiedHistoryFilters("ordinary", At, At.AddTicks(1), "failed");
        var seen = new List<string>(); string? cursor = null;
        do { var page = d.Page(limit, filters, cursor); Assert.InRange(page.Items.Count, 1, limit); seen.AddRange(page.Items.Select(i => i.RecordingId!)); cursor = page.NextCursor; } while (cursor is not null);
        Assert.Equal(ids.Length, seen.Count);
        // UTF-8 SQLite BINARY order differs from culture sorting, notably uppercase and non-ASCII.
        Assert.Equal(ids.OrderBy(id => Convert.ToHexString(Encoding.UTF8.GetBytes(id)), StringComparer.Ordinal), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        var empty = d.Page(limit, filters with { Status = "expired" }); Assert.Empty(empty.Items); Assert.Null(empty.NextCursor);
    }

    [Theory]
    [InlineData("all", "failed")] [InlineData("plan", "failed")] [InlineData("future_window", "failed")]
    [InlineData("ordinary", "")] [InlineData("ordinary", "FAILED")] [InlineData("ordinary", "failed,completed")]
    [InlineData("ordinary", " failed")] [InlineData("ordinary", "missing")]
    public void CoreAndBothGatewayScopesRejectInvalidFilters(string source, string status)
    {
        using var d = new UnifiedHistoryTestDatabase(); var filters = new UnifiedHistoryFilters(source, Status: status);
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(filters: filters));
        var headless = new UnifiedHistoryQueryService(d.Store, d.Ordinary, ordinaryOnly: true);
        Assert.Throws<UnifiedHistoryArgumentException>(() => headless.GetPage(Sid, Session, 20, filters, null));
    }

    [Fact]
    public void CursorBindsStatusSourceTimeIdentityDatabaseAndSupportsOmittedLegacyStatus()
    {
        using var d = new UnifiedHistoryTestDatabase(); using var other = new UnifiedHistoryTestDatabase();
        foreach (var id in new[] { "a", "b", "c" }) d.Ordinary.Register(d.Recording(id) with { Status = "failed" });
        var filters = new UnifiedHistoryFilters("ordinary", Status: "failed"); var cursor = d.Page(1, filters).NextCursor!;
        foreach (var changed in new[] { filters with { Status = "completed" }, filters with { Status = null }, new UnifiedHistoryFilters("all"), filters with { CreatedFrom = At } })
            Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(1, changed, cursor));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(1, filters, cursor, sid: "different"));
        Assert.Throws<UnifiedHistoryArgumentException>(() => d.Page(1, filters, cursor, session: "different"));
        Assert.Throws<UnifiedHistoryArgumentException>(() => other.Page(1, filters, cursor));
        var headless = new UnifiedHistoryQueryService(d.Store, d.Ordinary, true);
        Assert.Throws<UnifiedHistoryArgumentException>(() => headless.GetPage(Sid, Session, 1, filters, cursor));
        var old = d.Page(1, new("ordinary")).NextCursor!;
        var body = old.Split('.')[0]; var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/') + new string('=', (4-body.Length%4)%4))))!;
        Assert.Equal(2301, json["version"]!.GetValue<int>());
        Assert.Null(json["filters"]!["status"]); json["filters"]!.AsObject().Remove("status"); json["filters"]!.AsObject().Remove("hasValidStatus");
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString()); body = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var legacy = body + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        Assert.Equal("b", Assert.Single(d.Page(1, new("ordinary"), legacy).Items).RecordingId);
        Assert.Equal("b", Assert.Single(d.Page(1, new("ordinary"), old).Items).RecordingId);
    }

    [Fact]
    public void StatesAreLivePerPageButInsertedMembersStayOutsideOriginalWatermark()
    {
        using var d = new UnifiedHistoryTestDatabase();
        foreach (var id in new[] { "a", "b", "d" }) d.Ordinary.Register(d.Recording(id) with { Status = "failed" });
        foreach (var id in new[] { "A", "c" }) d.SeedOrdinary(id);
        var filters = new UnifiedHistoryFilters("ordinary", Status: "failed"); var first = d.Page(1, filters); Assert.Equal("a", first.Items[0].RecordingId);
        d.Execute("UPDATE ordinary_recording_history SET status_code='preparing' WHERE recording_id='b'; UPDATE ordinary_recording_history SET status_code='failed' WHERE recording_id IN ('A','c');");
        d.Ordinary.Register(d.Recording("B") with { Status = "failed" });
        d.Ordinary.Register(d.Recording("z", At.AddDays(-1)) with { Status = "failed" });
        var next = d.Page(20, filters, first.NextCursor); Assert.Equal(new[] { "c", "d" }, next.Items.Select(i => i.RecordingId)); Assert.Null(next.NextCursor);
        Assert.Equal(new[] { "A", "B", "a", "c", "d", "z" }, d.Page(20, filters).Items.Select(i => i.RecordingId));
    }

    [Fact]
    public void V24MigrationChecksumCannotBeTamperedOnReopen()
    {
        using var d=new UnifiedHistoryTestDatabase();d.Execute("UPDATE schema_migrations SET definition_checksum=$checksum WHERE version=24;",("$checksum",new string('0',64)));
        Assert.Throws<SqliteOperationalStoreException>(()=>d.Store.Initialize());
    }

    [Theory]
    [InlineData("")]
    [InlineData("CREATE INDEX idx_ordinary_recording_history_status_created ON ordinary_recording_history(created_at_utc DESC,status_code,recording_id COLLATE BINARY ASC);")]
    [InlineData("CREATE INDEX idx_ordinary_recording_history_status_created ON ordinary_recording_history(status_code,created_at_utc ASC,recording_id COLLATE BINARY ASC);")]
    [InlineData("CREATE INDEX idx_ordinary_recording_history_status_created ON ordinary_recording_history(status_code,created_at_utc DESC,recording_id COLLATE NOCASE ASC);")]
    [InlineData("CREATE INDEX idx_ordinary_recording_history_status_created ON ordinary_recording_history(status_code,created_at_utc DESC,recording_id COLLATE BINARY ASC) WHERE status_code='failed';")]
    [InlineData("CREATE UNIQUE INDEX idx_ordinary_recording_history_status_created ON ordinary_recording_history(status_code,created_at_utc DESC,recording_id COLLATE BINARY ASC);")]
    public void MissingOrMalformedV24IndexFailsClosedOnReadAndReopen(string replacement)
    {
        using var d = new UnifiedHistoryTestDatabase(); d.Execute("DROP INDEX idx_ordinary_recording_history_status_created;" + replacement);
        Assert.Throws<UnifiedHistoryUnavailableException>(() => d.Page(filters: new("ordinary", Status: "failed")));
        Assert.Throws<SqliteOperationalStoreException>(() => d.Store.Initialize());
    }
}
