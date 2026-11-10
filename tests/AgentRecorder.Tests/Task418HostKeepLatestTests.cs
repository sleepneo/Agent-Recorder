using System.Net;
using System.Text.Json;
using AgentRecorder.Api;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class Task418HostKeepLatestTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(9);
    private const string Kept = "within_host_keep_latest";
    private const string Invalid = "host_keep_latest_configuration_invalid";
    private const string Unavailable = "host_keep_latest_unavailable";

    [Theory]
    [InlineData(null, 0, "default", null)]
    [InlineData("0", 0, "environment", null)]
    [InlineData("001", 1, "environment", null)]
    [InlineData("1000", 1000, "environment", null)]
    [InlineData("", null, "environment", "invalid_integer")]
    [InlineData(" 1", null, "environment", "invalid_integer")]
    [InlineData("+1", null, "environment", "invalid_integer")]
    [InlineData("-1", null, "environment", "invalid_integer")]
    [InlineData("1.0", null, "environment", "invalid_integer")]
    [InlineData("１", null, "environment", "invalid_integer")]
    [InlineData("1\0", null, "environment", "invalid_integer")]
    [InlineData("1001", null, "environment", "out_of_range")]
    [InlineData("999999999999999999999999", null, "environment", "out_of_range")]
    public void StartupParserAcceptsOnlyAsciiCountsInRange(string? input, int? count, string source, string? invalid)
    {
        var settings = RetentionHostKeepLatestProtection.Parse(input);
        Assert.Equal(count, settings.EffectiveCount);
        Assert.Equal(source, settings.Source);
        Assert.Equal(invalid, settings.InvalidReason);
    }

    [Fact]
    public void StartupSettingIsFrozenAcrossEnvironmentChanges()
    {
        var initial = RetentionHostKeepLatestProtection.Startup;
        var name = RetentionHostKeepLatestProtection.EnvironmentVariable;
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, initial.EffectiveCount == 1000 ? "1" : "1000");
            Assert.Same(initial, RetentionHostKeepLatestProtection.Startup);
        }
        finally { Environment.SetEnvironmentVariable(name, original); }
    }

    [Theory]
    [InlineData(1, 0, "aardvark", Kept)]
    [InlineData(2, 1, "zzz", Kept)]
    [InlineData(1, 2, "zzz", "within_keep_latest")]
    public async Task NoPolicyAndSmallerPolicyCannotLowerHostFloorAndLargerPolicyStillApplies(
        int hostCount, int policyCount, string tiedId, string reason)
    {
        using var f = new DeletionRequestFixture(real: true, hostProtection: Setting(hostCount));
        AddTied(f, tiedId);
        var body = policyCount == 0 ? null : PolicyBody(f, policyCount);
        var id = RequestId(await f.Post(body: body));
        AssertProtected(await f.Finish(id), reason);
        Assert.Equal(0, f.F.Ui.Begins);
        Assert.Equal(0, f.F.Native.Calls);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));
        Assert.Equal(id, RequestId(await f.Post(body: body)));
        AssertProtected(await f.Finish(id), reason);
        AssertOriginal(f.F.Row, f.F.Bytes);
    }

    [Fact]
    public void OneOrderingReadPerSnapshotAndHostReasonPrecedesRequestCutoff()
    {
        using var f = new DeletionRequestFixture(hostProtection: Setting(1));
        AddTied(f, "zzz");
        Assert.NotNull(f.F.Db.Ordinary.Read("zzz"));
        var history = f.F.Db.Query;
        int reads = 0;
        history.LatestReadForTests = () => reads++;
        using var connection = f.F.Db.Store.OpenReadOnlyConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        var cutoff = f.F.Row.CompletedAtUtc!.Value;
        var policy = new RetentionDeletionPolicy(cutoff, 2);
        Assert.Equal("within_keep_latest", Setting(1).Check(policy, history, connection, transaction,
            f.F.Row.RecordingId, cutoff, default));
        Assert.Equal(1, reads);
        Assert.Equal(Kept, Setting(2).Check(policy, history, connection, transaction, f.F.Row.RecordingId, cutoff, default));
        Assert.Equal(2, reads);
        Assert.Equal("completion_not_before_cutoff", Setting(1).Check(new(cutoff, 1), history, connection,
            transaction, f.F.Row.RecordingId, cutoff, default));
        Assert.Equal(3, reads);
        Assert.Null(Setting(0).Check(null, history, connection, transaction, f.F.Row.RecordingId, cutoff, default));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task InvalidSettingBlocksNewExecutionButNotQueriesOrReplayUnderAChangedSetting()
    {
        using var f = new DeletionRequestFixture(real: true, hostProtection: RetentionHostKeepLatestProtection.Parse("1001"));
        var cap = await Capabilities(f.Client);
        var setting = cap.GetProperty("retention_deletion").GetProperty("host_keep_latest");
        Assert.True(setting.GetProperty("enforcement_supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, setting.GetProperty("effective_count").ValueKind);
        Assert.Equal("out_of_range", setting.GetProperty("invalid_reason").GetString());
        var id = await f.Create();
        var done = await f.Finish(id);
        AssertProtected(done, Invalid);
        Assert.Equal("fix_host_keep_latest_configuration_and_restart", done.GetProperty("suggested_action").GetString());
        Assert.Equal(0, f.F.Ui.Begins);
        Assert.Equal(0, f.F.Native.Calls);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM ordinary_deletion_request_policies;"));

        using var reopened = Reopen(f, Setting(0));
        var replay = await reopened.RequestAsync(f.F.Row.RecordingId, "owned-key");
        Assert.Equal(id, replay.DeletionRequestId);
        Assert.Equal(Invalid, replay.ReasonCode);
        Assert.Equal("finished", replay.RequestState);
        Assert.False(replay.FileActionPerformed);
        Assert.True(await reopened.WaitForIdleAsync().WaitAsync(Bound));
        Assert.Equal(0, f.F.Ui.Begins);
        Assert.Equal(0, f.F.Native.Calls);
        AssertOriginal(f.F.Row, f.F.Bytes);
    }

    [Fact]
    public async Task HeadlessCapabilityShowsTheSettingButCannotExecuteDeletion()
    {
        using var f = new DeletionRequestFixture(headless: true, hostProtection: Setting(7));
        var cap = (await Capabilities(f.Client)).GetProperty("retention_deletion");
        Assert.False(cap.GetProperty("supported").GetBoolean());
        var setting = cap.GetProperty("host_keep_latest");
        Assert.False(setting.GetProperty("enforcement_supported").GetBoolean());
        Assert.Equal(7, setting.GetProperty("effective_count").GetInt32());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Post()).Status);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));
    }

    [Theory]
    [InlineData("latest", Kept)]
    [InlineData("unknown", Unavailable)]
    public async Task AfterApprovalRechecksCurrentOrderingInsideTheProtectedTransaction(string change, string reason)
    {
        var ledger = new DeletionRequestFixture.Io();
        using var f = new DeletionRequestFixture(real: true, ledger: ledger, hostProtection: Setting(1));
        AddTied(f, "zzz");
        var id = await f.Create();
        await f.F.Ui.Entered.Task.WaitAsync(Bound);
        ledger.OnStep = (step, _, _) =>
        {
            if (step != "prepare_after_close") return;
            f.F.Db.Corrupt(change == "latest"
                ? "UPDATE ordinary_recording_history SET status_code='failed' WHERE recording_id='zzz';"
                : "UPDATE ordinary_recording_history SET completed_at_utc=NULL WHERE recording_id='zzz';");
        };
        await f.Approve();
        var done = await f.Finish(id);
        AssertProtected(done, reason);
        Assert.Equal("not_attempted", done.GetProperty("disposition_progress").GetString());
        Assert.Equal("committed", done.GetProperty("ledger_settlement").GetString());
        Assert.Equal(2, f.Scalar("SELECT count(*) FROM ordinary_deletion_host_rejections;"));
        Assert.Equal(1, f.F.Ui.Begins);
        Assert.Equal(0, f.F.Native.Calls);
        AssertOriginal(f.F.Row, f.F.Bytes);
        using var reopened = Reopen(f, Setting(0));
        Assert.Equal(reason, (await reopened.RequestAsync(f.F.Row.RecordingId, "owned-key")).ReasonCode);
        Assert.True(await reopened.WaitForIdleAsync().WaitAsync(Bound));
        Assert.Equal(0, f.F.Native.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task EligibleItemStillRequiresLocalApprovalAndUsesTheOriginalDisposition(int hostCount)
    {
        using var f = new DeletionRequestFixture(real: true, hostProtection: Setting(hostCount));
        if (hostCount > 0) AddTied(f, "zzz");
        var id = await f.Create();
        await f.F.Ui.Entered.Task.WaitAsync(Bound);
        Assert.Equal(0, f.F.Native.Calls);
        await f.Approve();
        Assert.True((await f.Finish(id)).GetProperty("file_action_performed").GetBoolean());
        Assert.Equal(1, f.F.Native.Calls);
        f.F.AssertNotAddressable();
    }

    [Fact]
    public async Task PolicyFreeOrderedBatchStopsAtProtectedChildAndNeverRequestsTheRemainder()
    {
        using var f = new BatchDeletion388Fixture(3, real: true, hostProtection: Setting(1));
        var order = new[] { f.Ids[0], f.Ids[2], f.Ids[1] };
        var body = JsonSerializer.Serialize(new { recording_ids = order });
        var cap = (await Capabilities(f.Client)).GetProperty("retention_batch_deletion").GetProperty("host_keep_latest");
        Assert.True(cap.GetProperty("enforcement_supported").GetBoolean());
        Assert.Equal(1, cap.GetProperty("effective_count").GetInt32());
        var id = RequestId(await f.Post(body: body), batch: true);
        var first = await f.Prompts.Next();
        Assert.Equal(order[0], first.Intent.Facts.RecordingId);
        Assert.Equal(0, f.Native.Calls);
        first.Decide(LocalRetentionUiChoice.Approve);
        var done = await f.Finish(id);
        Assert.Equal(Kept, done.StopReason);
        Assert.Equal("disposition_accepted", done.Items[0].State);
        Assert.Equal(Kept, done.Items[1].Request!.ReasonCode);
        Assert.False(done.Items[1].Request!.FileActionPerformed);
        Assert.Null(done.Items[1].Request!.OperationId);
        Assert.Equal("not_requested", done.Items[2].State);
        Assert.Equal(1, f.Prompts.Begins);
        Assert.Equal(1, f.Native.Calls);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));
        AssertOriginal(f.Rows[1], f.F.Bytes);
        AssertOriginal(f.Rows[2], f.F.Bytes);
        Assert.Equal(id, RequestId(await f.Post(body: body), batch: true));
        Assert.Equal(Kept, (await f.Finish(id)).StopReason);
        Assert.Equal(1, f.Prompts.Begins);
        Assert.Equal(1, f.Native.Calls);
    }

    private static RetentionHostKeepLatestProtection Setting(int count) =>
        RetentionHostKeepLatestProtection.Parse(count.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void AddTied(DeletionRequestFixture f, string id) => f.F.Db.Ordinary.Register(
        f.F.Db.Recording(id, completed: true) with { CompletedAtUtc = f.F.Row.CompletedAtUtc, BundleRegistration = new(null) });

    private static string PolicyBody(DeletionRequestFixture f, int n) => JsonSerializer.Serialize(new
    {
        recording_id = f.F.Row.RecordingId,
        policy = new { completed_before = f.F.Clock.GetUtcNow().AddSeconds(-1).ToString("O"), keep_latest_count = n }
    });

    private static string RequestId((HttpStatusCode Status, JsonElement Json) response, bool batch = false)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.Status);
        return response.Json.GetProperty("data").GetProperty(batch ? "batch_deletion_request_id" : "deletion_request_id").GetString()!;
    }

    private static void AssertProtected(JsonElement result, string reason)
    {
        Assert.Equal("finished", result.GetProperty("request_state").GetString());
        Assert.Equal(reason, result.GetProperty("reason_code").GetString());
        Assert.False(result.GetProperty("file_action_performed").GetBoolean());
    }

    private static void AssertOriginal(OrdinaryRecordingHistorySnapshot row, byte[] bytes)
    {
        Assert.Equal(bytes, File.ReadAllBytes(row.OutputPath));
        Assert.Equal(row.PublicationReceipt!.Anchor.Identity,
            WindowsReadOnlyFileMetadataProbe.Instance.Observe(row.OutputPath).Before!.Identity);
    }

    private static async Task<JsonElement> Capabilities(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync("/api/v1/capabilities")).RootElement.GetProperty("data").Clone();

    private static RetentionDeletionCoordinator Reopen(DeletionRequestFixture f, RetentionHostKeepLatestProtection setting) =>
        new(f.F.Db.Store, UnifiedHistoryTestDatabase.Sid, UnifiedHistoryTestDatabase.Session, f.F.Videos,
            f.F.Service(hostProtection: setting), f.F.Clock, admission: new(), queryAdmission: new());
}
