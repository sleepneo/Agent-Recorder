using System.Net;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentRecorder.Api;
using AgentRecorder.App;
using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class RecurringHistoryCursorTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 100)]
    [InlineData(true, 100)]
    public void QueryAndProductionGatewayTraverseGeneratedIds(bool durable, int limit)
    {
        using var f = new Fixture(durable);
        var expected = f.Ids();
        var before = f.Db.State();
        var actual = new List<string>();
        string? cursor = null;
        var seen = new HashSet<string>();
        do
        {
            var query = new PlanExecutionStatusQueryService(f.Reopen());
            var candidates = -1; var validated = -1;
            query.HistoryCandidateRowsSelectedForTests = count => candidates = count;
            query.HistoryEvidenceRowsValidatedForTests = count => validated = count;
            var page = query.GetHistory(f.PlanId, f.Sid, f.Binding, limit, cursor)!;
            Assert.Equal(Math.Min(limit + 1, expected.Length - actual.Count), candidates);
            Assert.Equal(Math.Min(limit, expected.Length - actual.Count), validated);
            actual.AddRange(page.Items.Select(item => item.OccurrenceId));
            cursor = page.NextCursor;
            if (cursor is not null) Assert.True(seen.Add(cursor), "A cursor repeated.");
            Assert.InRange(actual.Count, 1, expected.Length);
        } while (cursor is not null);
        Assert.Equal(expected, actual);
        var gateway = new PlanExecutionStatusGateway(new(f.Reopen()));
        actual.Clear(); cursor = null; seen.Clear();
        do
        {
            var page = gateway.GetPage(f.PlanId, limit, cursor)!;
            actual.AddRange(page.Items.Select(item => item.OccurrenceId));
            cursor = page.NextCursor;
            if (cursor is not null) Assert.True(seen.Add(cursor), "A gateway cursor repeated.");
            Assert.InRange(actual.Count, 1, expected.Length);
        } while (cursor is not null);
        Assert.Equal(expected, actual);
        f.AssertReadOnly(before);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 100)]
    [InlineData(true, 100)]
    public async Task ActualApiTraversesGeneratedIdsWithUriEncodedCursor(bool durable, int limit)
    {
        using var f = new Fixture(durable);
        using var host = new Host(f.Reopen());
        var expected = f.Ids();
        var before = f.Db.State();
        var actual = new List<string>();
        var seen = new HashSet<string>();
        string? cursor = null;
        do
        {
            using var response = await host.Http.GetAsync(HistoryUrl(f.PlanId, limit, cursor));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            actual.AddRange(data.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("occurrence_id").GetString()!));
            cursor = data.GetProperty("next_cursor").GetString();
            if (cursor is not null) Assert.True(seen.Add(cursor), "An API cursor repeated.");
            Assert.InRange(actual.Count, 1, expected.Length);
        } while (cursor is not null);
        Assert.Equal(expected, actual);
        f.AssertReadOnly(before);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProductionAdvancementAfterFirstPageDoesNotEnterOldWatermark(bool durable, bool api)
    {
        using var f = new Fixture(durable);
        using var host = new Host(f.Reopen());
        var expected = f.Ids();
        var before = f.Db.State();
        var first = await Fetch(null, 1);
        Assert.NotNull(first.Cursor);
        f.AssertReadOnly(before);
        var late = f.Advance();
        var afterAdvancement = f.Db.State();
        var ids = first.Ids.ToList();
        var cursor = first.Cursor;
        var seen = new HashSet<string>();
        while (cursor is not null)
        {
            Assert.True(seen.Add(cursor));
            var page = await Fetch(cursor, 1);
            ids.AddRange(page.Ids); cursor = page.Cursor;
            Assert.InRange(ids.Count, 1, expected.Length);
        }
        Assert.Equal(expected, ids);
        Assert.DoesNotContain(late, ids);
        var fresh = await Fetch(null, 100);
        Assert.Equal(f.Ids(), fresh.Ids);
        Assert.Contains(late, fresh.Ids);
        f.AssertReadOnly(afterAdvancement);

        async Task<Page> Fetch(string? next, int limit)
        {
            if (api) return await ApiPage(host.Http, f.PlanId, limit, next);
            var query = new PlanExecutionStatusQueryService(f.Reopen());
            query.HistoryCandidateRowsSelectedForTests = count => Assert.InRange(count, 0, limit + 1);
            query.HistoryEvidenceRowsValidatedForTests = count => Assert.InRange(count, 0, limit);
            var page = query.GetHistory(f.PlanId, f.Sid, f.Binding, limit, next)!;
            return new(page.Items.Select(item => item.OccurrenceId).ToArray(), page.NextCursor);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortOnceIdsWithEqualWindowSortByBinaryAndFreezeLaterTiedInsertion(bool api)
    {
        using var f = new PlanStatusDurableBindingTests.Fixture();
        var plan = new SqlitePlanDefinitionRepository(f.Store).Get(f.PlanId);
        var repository = new SqlitePlanOccurrenceRepository(f.Store);
        // Use supported scheduled Core snapshots and normal repository inserts.
        // Do not rewrite recurring slots, scope digests, times, or approvals.
        foreach (var id in new[] { "tie-z", "tie-A", "tie_0", "tie.0" })
            repository.Insert(PlanOccurrence.CreateFor(plan, id, f.Now.AddMinutes(1), f.Now.AddMinutes(2), f.Now));
        using var host = new Host(new(f.Store.DatabasePath));
        var before = DatabaseState(f.Store);
        var expected = new[] { f.Prepared.OccurrenceId!, "tie-z", "tie-A", "tie_0", "tie.0" }.Order(StringComparer.Ordinal).ToArray();
        var first = await Fetch(null, 1);
        Assert.Equal(expected[0], Assert.Single(first.Ids));
        Assert.Equal(before, DatabaseState(f.Store));
        const string late = "tie_1"; // Same time, sorts after the first page: rowid watermark must exclude it.
        repository.Insert(PlanOccurrence.CreateFor(plan, late, f.Now.AddMinutes(1), f.Now.AddMinutes(2), f.Now));
        var afterInsert = DatabaseState(f.Store);
        var ids = first.Ids.ToList(); var cursor = first.Cursor; var seen = new HashSet<string>();
        while (cursor is not null)
        {
            Assert.True(seen.Add(cursor));
            var page = await Fetch(cursor, 2); ids.AddRange(page.Ids); cursor = page.Cursor;
            Assert.InRange(ids.Count, 1, expected.Length);
        }
        Assert.Equal(expected, ids);
        var fresh = await Fetch(null, 100);
        Assert.Equal(expected.Append(late).Order(StringComparer.Ordinal), fresh.Ids);
        Assert.Equal(afterInsert, DatabaseState(f.Store));
        Assert.Equal(0L, f.Scalar("SELECT COUNT(*) FROM recording_runs;"));

        async Task<Page> Fetch(string? next, int limit)
        {
            if (api) return await ApiPage(host.Http, f.PlanId, limit, next);
            var query = new PlanExecutionStatusQueryService(new(f.Store.DatabasePath));
            query.HistoryCandidateRowsSelectedForTests = count => Assert.InRange(count, 0, limit + 1);
            query.HistoryEvidenceRowsValidatedForTests = count => Assert.InRange(count, 0, limit);
            var page = query.GetHistory(f.PlanId, f.Sid, f.Binding, limit, next)!;
            return new(page.Items.Select(item => item.OccurrenceId).ToArray(), page.NextCursor);
        }
    }

    public static IEnumerable<object?[]> InvalidCursorFields()
    {
        var prefix = RecurringOccurrenceIdentity.Prefix;
        foreach (var id in new[] { "", " ", "unknown-occurrence/v1:" + new string('a', 64),
            "recurring-occurrence/v2:" + new string('a', 64), "Recurring-occurrence/v1:" + new string('a', 64),
            prefix + new string('a', 63), prefix + new string('a', 65), prefix + new string('A', 64), prefix + new string('g', 64),
            prefix + new string('a', 64) + "\0", prefix + new string('a', 63) + "\n", "short\0suffix", "short\nline",
            "plain/slash", "plain:colon", "plain\\backslash", "..\\file", "../file", "C:\\file", "plain%2Fslash",
            "recurring-occurrence%2Fv1%3A" + new string('a', 64), "short?query", "short#fragment", "short' OR 1=1--", new string('a', 129) })
            yield return new object?[] { "LastOccurrenceId", id };
        yield return new object?[] { "LastOccurrenceId", null };
        yield return new object?[] { "Version", "-1" };
        yield return new object?[] { "Version", "2" };
        yield return new object?[] { "PlanId", "other-plan" };
        yield return new object?[] { "IdentityDigest", new string('0', 64) };
        yield return new object?[] { "Watermark", "-1" };
        yield return new object?[] { "Watermark", "not-a-number" };
        yield return new object?[] { "Watermark", "9223372036854775808" };
        foreach (var time in new[] { "", "-1", "+1", "not-a-time", "3155378976000000000", "9223372036854775808" })
            yield return new object?[] { "LastWindowStart", time };
        yield return new object?[] { "LastWindowStart", null };
    }

    [Theory]
    [MemberData(nameof(InvalidCursorFields))]
    public async Task InvalidCursorFieldsAreRejectedByQueryGatewayAndActualApi(string field, string? value)
    {
        using var f = new Fixture(true);
        var query = new PlanExecutionStatusQueryService(f.Reopen());
        var valid = query.GetHistory(f.PlanId, f.Sid, f.Binding, 1)!.NextCursor!;
        var payload = Decode(valid);
        payload[field] = field is "Version" or "Watermark"
            ? value == "not-a-number" ? JsonValue.Create(value) : JsonNode.Parse(value!)
            : value is null ? null : JsonValue.Create(value);
        var bad = Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        await AssertRejected(f, bad);
    }

    [Theory]
    [InlineData("bad-base64")]
    [InlineData("bad-json")]
    [InlineData("null-json")]
    [InlineData("array-json")]
    [InlineData("bad-checksum")]
    [InlineData("missing-fields")]
    [InlineData("overlong")]
    [InlineData("empty")]
    public async Task MalformedCursorEnvelopeKeepsHttpErrorContract(string kind)
    {
        using var f = new Fixture(false);
        var valid = new PlanExecutionStatusQueryService(f.Reopen()).GetHistory(f.PlanId, f.Sid, f.Binding, 1)!.NextCursor!;
        var bad = kind switch
        {
            "bad-base64" => "%%%." + new string('a', 64),
            "bad-json" => Encode(Encoding.UTF8.GetBytes("{")),
            "null-json" => Encode(Encoding.UTF8.GetBytes("null")),
            "array-json" => Encode(Encoding.UTF8.GetBytes("[]")),
            "bad-checksum" => valid[..^1] + (valid[^1] == '0' ? '1' : '0'),
            "missing-fields" => Encode(Encoding.UTF8.GetBytes("{}")),
            "overlong" => new string('a', 1025),
            _ => "",
        };
        await AssertRejected(f, bad, bad.Length is 0 or > 1024);
    }

    [Fact]
    public async Task ForeignPlanAndIdentityCursorsAreBoundAndUnknownOrForeignPlansStayHidden()
    {
        using var f = new Fixture(true);
        var other = f.CreatePlan("other-plan"); f.Advance(other);
        var otherLogin = f.CreatePlan("other-login", DurableAuthorizationIdentity.Format(f.Sid, 100, 54321, 1)!); f.Advance(otherLogin);
        var foreign = f.CreatePlan("foreign-user", "foreign-session", "S-1-5-18"); f.Advance(foreign);
        var query = new PlanExecutionStatusQueryService(f.Reopen());
        var valid = query.GetHistory(f.PlanId, f.Sid, f.Binding, 1)!.NextCursor!;
        var foreignIdentity = Decode(valid);
        foreignIdentity["IdentityDigest"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(f.Sid + "\0other-login"))).ToLowerInvariant();
        await AssertRejected(f, Encode(Encoding.UTF8.GetBytes(foreignIdentity.ToJsonString())));
        var before = f.Db.State();
        Assert.Throws<PlanExecutionHistoryCursorException>(() => query.GetHistory(other, f.Sid, f.Binding, 1, valid));
        Assert.Null(query.GetHistory(f.PlanId, f.Sid, "unowned-login", 1, valid));
        using var host = new Host(f.Reopen());
        foreach (var plan in new[] { other, otherLogin })
        {
            using var response = await host.Http.GetAsync(HistoryUrl(plan, 1, valid));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("INVALID_ARGUMENT", await ErrorCode(response));
        }
        foreach (var plan in new[] { foreign, "unknown-plan" })
        {
            using var response = await host.Http.GetAsync(HistoryUrl(plan, 1, valid));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("PLAN_NOT_FOUND", await ErrorCode(response));
        }
        f.AssertReadOnly(before);
    }

    private static async Task AssertRejected(Fixture f, string cursor, bool argument = false)
    {
        var before = f.Db.State();
        var query = new PlanExecutionStatusQueryService(f.Reopen());
        query.HistoryCandidateRowsSelectedForTests = _ => Assert.Fail("An invalid cursor reached occurrence selection.");
        query.HistoryEvidenceRowsValidatedForTests = _ => Assert.Fail("An invalid cursor reached evidence validation.");
        if (argument)
            Assert.Equal("invalid_argument", Assert.Throws<Phase3PersistenceException>(() => query.GetHistory(f.PlanId, f.Sid, f.Binding, 1, cursor)).Code);
        else Assert.Throws<PlanExecutionHistoryCursorException>(() => query.GetHistory(f.PlanId, f.Sid, f.Binding, 1, cursor));
        var gateway = new PlanExecutionStatusGateway(new(f.Reopen()));
        if (argument) Assert.Throws<Phase3PersistenceException>(() => gateway.GetPage(f.PlanId, 1, cursor));
        else Assert.Throws<ArgumentException>(() => gateway.GetPage(f.PlanId, 1, cursor));
        using var host = new Host(f.Reopen());
        using var response = await host.Http.GetAsync(HistoryUrl(f.PlanId, 1, cursor));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("INVALID_ARGUMENT", await ErrorCode(response));
        Assert.DoesNotContain(f.Sid, body); Assert.DoesNotContain(f.Binding, body); Assert.DoesNotContain(cursor.Length > 0 ? cursor : "no-cursor-token", body);
        f.AssertReadOnly(before);
    }

    private static JsonObject Decode(string cursor)
    {
        var body = cursor.Split('.')[0].Replace('-', '+').Replace('_', '/');
        body += new string('=', (4 - body.Length % 4) % 4);
        return JsonNode.Parse(Convert.FromBase64String(body))!.AsObject();
    }
    private static string Encode(byte[] payload) => Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." +
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    private static async Task<string> ErrorCode(HttpResponseMessage response)
    { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!; }
    private sealed record Page(string[] Ids, string? Cursor);
    private static async Task<Page> ApiPage(HttpClient http, string plan, int limit, string? cursor)
    {
        using var response = await http.GetAsync(HistoryUrl(plan, limit, cursor)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = document.RootElement.GetProperty("data");
        return new(data.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("occurrence_id").GetString()!).ToArray(), data.GetProperty("next_cursor").GetString());
    }
    private static string DatabaseState(SqliteOperationalStore store)
    {
        using var connection = store.OpenReadOnlyConnection();
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = command.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var rows = new List<string>();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1;";
            using var reader = command.ExecuteReader(); while (reader.Read()) rows.Add(table + JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        }
        return string.Join("\n", rows);
    }

    private static string HistoryUrl(string plan, int limit, string? cursor) =>
        $"api/v1/plans/{plan}/history?limit={limit}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));

    private sealed class Fixture : IDisposable
    {
        internal readonly RecurringPlanSetupTestFixture Db = new();
        internal string Sid { get; }
        internal string Binding { get; }
        internal string PlanId { get; }
        private readonly Dictionary<string, int> _versions = new();
        internal Fixture(bool durable)
        {
            using var identity = WindowsIdentity.GetCurrent(); Sid = identity.User!.Value;
            Binding = durable ? DurableAuthorizationIdentity.Format(Sid, 99, 12345, 1)! : "legacy-recurring-cursor";
            PlanId = CreatePlan("cursor-plan");
            for (var index = 0; index < 3; index++) Advance();
        }
        internal string CreatePlan(string intent, string? binding = null, string? sid = null)
        {
            binding ??= Binding; sid ??= Sid;
            var schedule = RecurringPlanSchedule.CreateDaily("China Standard Time", new(2026, 1, 1), new(2026, 1, 6), new(9, 30),
                6, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
            var now = RecurringPlanSetupTestFixture.At(10);
            var snapshot = RecurringSetupIntentSnapshot.CreateForTrustedSetupAdapter(intent, intent + "-key", sid, binding,
                now, now.AddDays(7), schedule, 6, TimeSpan.FromSeconds(180), now.AddDays(7),
                Db.Request().OutputDirectory, "cursor-" + intent);
            var created = new RecurringSetupIntentService(Db.Store, () => now).CreateOrGet(snapshot);
            Assert.True(created.Result == RecurringSetupIntentResultStatus.Created, created.Reason);
            var selection = RecurringFixedRegionSelectionSnapshot.CreateForTrustedLocalSelectionAdapter(intent, sid, binding, now,
                "stable-display", new(100, 200, 1920, 1080), new(10, 20, 640, 480), 96, 96, 1920, 1080,
                AuthorizedDisplayOrientation.Landscape, new string('a', 64));
            Assert.Equal(RecurringSetupPreparationResultStatus.Prepared, new RecurringSetupPreparationService(Db.Store, () => now).Prepare(selection).Status);
            Db.Activate(intent, sid, binding);
            return Db.Query.Get(intent, sid, binding)!.PlanId!;
        }
        internal string Advance(string? planId = null)
        {
            planId ??= PlanId;
            _versions.TryGetValue(planId, out var version);
            var result = new SqliteRecurringAdvancementTransaction(Db.Store).AdvanceOne(planId, 1,
                "advance-" + planId + "-" + version, version, RecurringPlanSetupTestFixture.At(0), RecurringPlanSetupTestFixture.At(10));
            Assert.Equal("scheduled", result.ResultCode);
            var id = result.OccurrenceIdentity!;
            Assert.StartsWith(RecurringOccurrenceIdentity.Prefix, id);
            Assert.Equal(RecurringOccurrenceIdentity.Prefix.Length + 64, id.Length);
            Assert.All(id[RecurringOccurrenceIdentity.Prefix.Length..], c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
            _versions[planId] = version + 1;
            return id;
        }
        internal SqliteOperationalStore Reopen() => new(Db.Store.DatabasePath);
        internal string[] Ids()
        {
            using var connection = Db.Store.OpenReadOnlyConnection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM plan_occurrences WHERE plan_id=$plan ORDER BY window_start_utc DESC,id COLLATE BINARY ASC;";
            command.Parameters.AddWithValue("$plan", PlanId);
            using var reader = command.ExecuteReader(); var ids = new List<string>(); while (reader.Read()) ids.Add(reader.GetString(0)); return ids.ToArray();
        }
        internal void AssertReadOnly(string before)
        {
            Assert.Equal(before, Db.State());
            foreach (var table in new[] { "recording_runs", "lease_uses", "recurring_lease_uses" })
                Assert.Equal(0L, Db.Scalar($"SELECT COUNT(*) FROM {table};"));
            Assert.False(Directory.Exists(Db.Request().OutputDirectory));
            Assert.Empty(Directory.GetFiles(Db.Root, "*.mp4", SearchOption.AllDirectories));
        }
        public void Dispose() => Db.Dispose();
    }

    private sealed class Host : IDisposable
    {
        private readonly RecordingEngine _engine;
        private readonly ApiServer _server;
        internal readonly HttpClient Http;
        internal Host(SqliteOperationalStore store)
        {
            var root = Path.GetDirectoryName(store.DatabasePath)!;
            ApiKeyAuth.InitializeForTesting(root);
            var audit = new AuditLogger(Path.Combine(root, "audit.jsonl")); _engine = new(audit);
            var gateway = new PlanExecutionStatusGateway(new(store));
            _server = new ApiServer(_engine, audit, new Tray(), readiness: null, autoStart: null, ffmpegPrewarmer: null, tracer: null,
                ensureContextStore: null, performanceSummaryProvider: null, standingPlanSetupGateway: null,
                recurringPlanSetupGateway: null, planExecutionStatusGateway: gateway, listenPort: 0, planExecutionHistoryGateway: gateway);
            _server.Start();
            Http = new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{_server.BoundPort}/"), Timeout = TimeSpan.FromSeconds(5) };
            Http.DefaultRequestHeaders.Add("X-Agent-Recorder-Key", ApiKeyAuth.CurrentApiKey);
        }
        public void Dispose() { _server.Stop(); _engine.Dispose(); Http.Dispose(); ApiKeyAuth.ResetForTesting(null); }
    }
    private sealed class Tray : ITrayContext
    {
        public string HostMode => "headless"; public bool SupportsRegionSelectionUi => false;
        public void RequestConfirmation(RecordingConfirmationPresentation p, Action<ConfirmationDecision> callback) => throw new InvalidOperationException();
        public void RequestRegionSelection(int seconds, Action<string, int, int, int, int, string, string> callback) => throw new InvalidOperationException();
        public void SetRecording(RecordingUiPresentation p) { } public void SetIdle(RecordingUiPresentation p) { } public void SetAllIdle() { } public void ShowError(string text) { }
    }
}
