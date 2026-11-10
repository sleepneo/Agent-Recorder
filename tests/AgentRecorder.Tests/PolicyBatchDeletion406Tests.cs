using System.Net;
using System.Text.Json;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class PolicyBatchDeletion406Tests
{
    private static RetentionDeletionPolicy Policy(BatchDeletion388Fixture fixture) =>
        new(fixture.F.Clock.GetUtcNow().AddMinutes(-1), 1);

    private static string Body(IEnumerable<string> ids, RetentionDeletionPolicy policy) =>
        JsonSerializer.Serialize(new
        {
            recording_ids = ids,
            policy = new { completed_before = policy.CompletedBefore.ToString("O"), keep_latest_count = policy.KeepLatestCount }
        });

    private static void AddKeeper(BatchDeletion388Fixture f)
    {
        var row = f.F.Db.Recording("unrequested-latest", completed: true) with
        {
            CompletedAtUtc = f.F.Clock.GetUtcNow().AddSeconds(-2),
            BundleRegistration = new(null)
        };
        f.F.Db.Ordinary.Register(row);
        Assert.NotNull(f.F.Db.Ordinary.Read(row.RecordingId));
    }

    private static void SetCompletion(BatchDeletion388Fixture f, string id, DateTimeOffset at) =>
        f.F.Db.Corrupt("UPDATE ordinary_recording_history SET completed_at_utc=$time WHERE recording_id=$id;",
            ("$time", at.UtcTicks), ("$id", id));

    private static async Task<string> Create(BatchDeletion388Fixture f, string body, string key = "policy-batch")
    {
        var response = await f.Post(key, body);
        Assert.Equal(HttpStatusCode.Accepted, response.Status);
        return response.Json.GetProperty("data").GetProperty("batch_deletion_request_id").GetString()!;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EachPositionRequiresItsOwnApprovalWithSameFrozenRule(int count)
    {
        using var f = new BatchDeletion388Fixture(count);
        AddKeeper(f);
        var policy = Policy(f);
        var ids = f.Ids.Reverse().ToArray();
        var body = Body(ids, policy);
        var batch = await Create(f, body);
        for (var n = 0; n < count; n++)
        {
            var prompt = await f.Prompts.Next();
            Assert.Equal(ids[n], prompt.Intent.Facts.RecordingId);
            Assert.Equal(policy, prompt.Intent.Facts.Policy);
            Assert.Equal(n + 1, f.Prompts.Begins);
            Assert.Equal(n, f.Native.Calls);
            prompt.Decide(LocalRetentionUiChoice.Approve);
        }
        var final = await f.Finish(batch);
        Assert.Equal("dispositions_accepted", final.BatchState);
        Assert.Equal(count, final.DispositionAcceptedCount);
        Assert.Equal(policy, final.Policy);
        Assert.Equal(ids, final.Items.Select(i => i.RecordingId));
        Assert.All(final.Items, i =>
        {
            Assert.True(i.ResourcesRetired);
            Assert.True(i.Request!.FileActionPerformed);
            Assert.Equal(policy, i.Request.Policy);
        });
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM ordinary_deletion_batch_policies;"));
        Assert.Equal(count, f.Scalar("SELECT count(*) FROM ordinary_deletion_batch_policy_children;"));
        Assert.Equal(count, f.Scalar("SELECT count(*) FROM ordinary_deletion_operation_policies;"));
        Assert.Equal(batch, await Create(f, body));
        Assert.True(await f.Batch.WaitForIdleAsync());
        Assert.Equal(count, f.Prompts.Begins);
        Assert.Equal(count, f.Native.Calls);
    }

    [Theory]
    [InlineData("latest", "within_keep_latest")]
    [InlineData("time", "completion_not_before_cutoff")]
    public async Task ProtectedFirstItemStopsWithoutAnyPromptOrDisposition(string change, string reason)
    {
        using var f = new BatchDeletion388Fixture();
        if (change == "time") AddKeeper(f);
        SetCompletion(f, f.Ids[0], f.F.Clock.GetUtcNow().AddSeconds(-10));
        var body = Body(f.Ids, Policy(f));
        var id = await Create(f, body);
        var result = await f.Finish(id);
        Assert.Equal("stopped", result.BatchState);
        Assert.Equal(reason, result.StopReason);
        Assert.Equal(reason, result.Items[0].ReasonCode);
        Assert.False(result.Items[0].Request!.FileActionPerformed);
        Assert.Null(result.Items[0].Request!.OperationId);
        Assert.Equal("not_requested", result.Items[1].State);
        Assert.Equal(0, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM ordinary_deletion_operations;"));
        Assert.Equal(id, await Create(f, body));
        Assert.True(await f.Batch.WaitForIdleAsync());
        Assert.Equal(0, f.Prompts.Begins);
        f.PreserveBytes();
    }

    [Theory]
    [InlineData("latest", "within_keep_latest")]
    [InlineData("time", "completion_not_before_cutoff")]
    [InlineData("reference", "child_non_success_or_unknown")]
    public async Task SecondItemPrecheckReadsChangesAfterFirstItemCommits(string change, string reason)
    {
        using var f = new BatchDeletion388Fixture(3);
        using var gate = new BatchDeletion388Fixture.Barrier();
        AddKeeper(f);
        var ids = new[] { f.Ids[0], f.Ids[2], f.Ids[1] };
        SetCompletion(f, ids[1], f.F.Clock.GetUtcNow().AddMinutes(-2));
        f.Batch.CheckpointForTests = (stage, ordinal) =>
        {
            if (stage == "item_committed" && ordinal == 0) gate.Block();
        };
        var id = await Create(f, Body(ids, Policy(f)));
        (await f.Prompts.Next()).Decide(LocalRetentionUiChoice.Approve);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try { ChangeFacts(f, ids[1], change); }
        finally { gate.Release(); }

        var result = await f.Finish(id);
        Assert.Equal(reason, result.StopReason);
        Assert.Equal("stopped", result.BatchState);
        Assert.Equal("disposition_accepted", result.Items[0].State);
        Assert.Equal("non_success", result.Items[1].State);
        Assert.Equal("not_requested", result.Items[2].State);
        Assert.Equal(1, result.DispositionAcceptedCount);
        Assert.Equal(1, f.Prompts.Begins);
        Assert.Equal(1, f.Native.Calls);
    }

    [Theory]
    [InlineData("latest", "within_keep_latest")]
    [InlineData("time", "completion_not_before_cutoff")]
    [InlineData("reference", "child_non_success_or_unknown")]
    public async Task AfterApprovalAndPrepareTheOriginalScopeChecksFreshFacts(string change, string reason)
    {
        var ledger = new DeletionRequestFixture.Io();
        using var f = new BatchDeletion388Fixture(3, ledger: ledger);
        using var gate = new BatchDeletion388Fixture.Barrier();
        AddKeeper(f);
        SetCompletion(f, f.Ids[0], f.F.Clock.GetUtcNow().AddMinutes(-2));
        ledger.OnStep = (stage, _, _) => { if (stage == "prepare_after_close") gate.Block(); };
        var id = await Create(f, Body(f.Ids, Policy(f)));
        (await f.Prompts.Next()).Decide(LocalRetentionUiChoice.Approve);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try { ChangeFacts(f, f.Ids[0], change); }
        finally { gate.Release(); }

        var result = await f.Finish(id);
        Assert.Equal(reason, result.StopReason);
        Assert.Equal("stopped", result.BatchState);
        Assert.False(result.Items[0].Request!.FileActionPerformed);
        Assert.Equal(2, result.NotRequestedCount);
        Assert.Equal(1, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
        f.PreserveBytes();
    }

    private static void ChangeFacts(BatchDeletion388Fixture f, string id, string change)
    {
        switch (change)
        {
            case "latest":
                f.F.Db.Corrupt("UPDATE ordinary_recording_history SET status_code='failed' WHERE recording_id='unrequested-latest';");
                break;
            case "time":
                SetCompletion(f, id, f.F.Clock.GetUtcNow().AddSeconds(-10));
                break;
            case "reference":
                f.F.Db.SeedFuture("new-protection", outputPath: f.Rows.Single(r => r.RecordingId == id).OutputPath);
                break;
        }
    }

    [Theory]
    [InlineData(false, "local_confirmation_rejected")]
    [InlineData(true, "local_confirmation_expired")]
    public async Task SecondItemRejectionOrExpiryKeepsOnlyFirstDisposition(bool expire, string reason)
    {
        using var f = new BatchDeletion388Fixture(3);
        AddKeeper(f);
        var id = await Create(f, Body(f.Ids, Policy(f)));
        (await f.Prompts.Next()).Decide(LocalRetentionUiChoice.Approve);
        var second = await f.Prompts.Next();
        if (expire) f.F.Clock.Advance(61);
        else second.Decide(LocalRetentionUiChoice.Reject);
        var result = await f.Finish(id);
        Assert.Equal(reason, result.StopReason);
        Assert.Equal(reason, result.Items[1].ReasonCode);
        Assert.Equal(expire ? "expired" : "rejected", result.Items[1].Request!.LocalConfirmationState);
        Assert.Equal(1, result.DispositionAcceptedCount);
        Assert.Equal("not_requested", result.Items[2].State);
        Assert.Equal(2, f.Prompts.Begins);
        Assert.Equal(1, f.Native.Calls);
    }

    [Fact]
    public async Task UnknownUiRetirementKeepsAdmissionAndDoesNotDispatchNext()
    {
        using var f = new BatchDeletion388Fixture();
        AddKeeper(f);
        var id = await Create(f, Body(f.Ids, Policy(f)));
        var prompt = await f.Prompts.Next();
        prompt.Decide(LocalRetentionUiChoice.Approve, retired: false);
        prompt.Retired.SetResult(false);
        Assert.False(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(7)));
        var result = (await f.Batch.ReadAsync(id))!;
        Assert.Equal("unknown", result.BatchState);
        Assert.False(result.BatchResourcesRetired);
        Assert.Equal("not_requested", result.Items[1].State);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Post("other-batch", Body(f.Ids, Policy(f)))).Status);
        Assert.NotNull(f.Batch.HostShutdownBusyReason);
        Assert.Equal(1, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
    }

    [Fact]
    public async Task CanonicalReplayConflictsOnPolicyPresenceCutoffCountOrderOrId()
    {
        using var f = new BatchDeletion388Fixture();
        AddKeeper(f);
        var policy = Policy(f);
        var body = Body(f.Ids, policy);
        var id = await Create(f, body);
        var prompt = await f.Prompts.Next();
        var utc = policy.CompletedBefore.ToString("O").Replace("+00:00", "Z", StringComparison.Ordinal);
        var canonicalReplay = $"  {{ \"policy\" : {{ \"keep_latest_count\" : 1, \"completed_before\" : \"{utc}\" }}, \"recording_ids\" : {JsonSerializer.Serialize(f.Ids)} }} ";
        Assert.Equal(id, await Create(f, canonicalReplay));
        foreach (var different in new[]
        {
            JsonSerializer.Serialize(new { recording_ids = f.Ids }),
            Body(f.Ids, new(policy.CompletedBefore, 2)),
            Body(f.Ids, new(policy.CompletedBefore.AddSeconds(-1), 1)),
            Body(f.Ids.Reverse(), policy),
            Body(new[] { f.Ids[0], "different-id" }, policy)
        })
            Assert.Equal(HttpStatusCode.Conflict, (await f.Post("policy-batch", different)).Status);
        prompt.Decide(LocalRetentionUiChoice.Reject);
        Assert.Equal(policy, (await f.Finish(id)).Policy);
        Assert.Equal(id, await Create(f, canonicalReplay));
        Assert.True(await f.Batch.WaitForIdleAsync());
        Assert.Equal(1, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
    }

    public static IEnumerable<object[]> InvalidPolicies()
    {
        const string cutoff = "\"completed_before\":\"2026-10-05T00:00:00Z\"";
        yield return new object[] { "null" };
        yield return new object[] { "[]" };
        yield return new object[] { "{}" };
        yield return new object[] { "{\"keep_latest_count\":1}" };
        yield return new object[] { "{" + cutoff + "}" };
        foreach (var value in new[] { "0", "1001", "1.0", "1e0", "-1", "\"1\"", "true", "null", "9223372036854775808" })
            yield return new object[] { "{" + cutoff + ",\"keep_latest_count\":" + value + "}" };
        yield return new object[] { "{" + cutoff + ",\"keep_latest_count\":1,\"keep_latest_count\":2}" };
        yield return new object[] { "{" + cutoff + ",\"keep_latest_count\":1,\"digest\":\"client-value\"}" };
        yield return new object[] { "{\"completed_before\":\"9999-01-01T00:00:00Z\",\"keep_latest_count\":1}" };
        yield return new object[] { "{\"completed_before\":\"2026-10-05T08:00:00+08:00\",\"keep_latest_count\":1}" };
        yield return new object[] { "{\"completed_before\":\"2026-10-05T00:00:00\",\"keep_latest_count\":1}" };
        yield return new object[] { "{\"completed_before\":\" 2026-10-05T00:00:00Z\",\"keep_latest_count\":1}" };
    }

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public async Task StrictPolicyInputNeverCreatesParentOrChild(string policy)
    {
        using var f = new BatchDeletion388Fixture();
        var body = "{\"recording_ids\":[\"candidate\"],\"policy\":" + policy + "}";
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Post(body: body)).Status);
        Assert.Empty(f.BatchIo.Calls);
        Assert.Empty(f.ChildIo.Calls);
        Assert.Equal(0, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
    }

    [Fact]
    public async Task DuplicateTopLevelPolicyIsRejectedAndMaxKeepCountIsAccepted()
    {
        using var f = new BatchDeletion388Fixture();
        var policy = new RetentionDeletionPolicy(Policy(f).CompletedBefore, 1000);
        var body = Body(f.Ids, policy);
        var duplicate = body.Insert(body.Length - 1, ",\"policy\":null");
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Post(body: duplicate)).Status);
        Assert.Empty(f.BatchIo.Calls);
        var id = await Create(f, body);
        var final = await f.Finish(id);
        Assert.Equal(policy, final.Policy);
        Assert.Equal("within_keep_latest", final.StopReason);
        Assert.Equal(0, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
    }

    [Theory]
    [InlineData("DROP TRIGGER ordinary_deletion_batch_policies_immutable;")]
    [InlineData("DELETE FROM schema_migrations WHERE version=50;")]
    public async Task MissingPolicySchemaOrReceiptFailsWithoutReadRepair(string sql)
    {
        using var f = new BatchDeletion388Fixture();
        var body = Body(f.Ids, new(Policy(f).CompletedBefore, 1000));
        var id = await Create(f, body);
        await f.Finish(id);
        f.F.Db.RawExecute(sql);
        var before = SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath, true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await QueryStatus(f, id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Post("policy-batch", body)).Status);
        Assert.Equal(before, SqlitePublicationIdentityReferenceTests.OldBytes(f.F.Db.Store.DatabasePath, true));
        Assert.Equal(0, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
    }

    [Fact]
    public async Task CapabilityAndLegacyResponseAgreeWithGatewayAndOptionalPolicy()
    {
        using var f = new BatchDeletion388Fixture();
        using var response = await f.Client.GetAsync("/api/v1/capabilities");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var capability = document.RootElement.GetProperty("data").GetProperty("retention_batch_deletion");
        Assert.True(capability.GetProperty("policy_supported").GetBoolean());
        Assert.True(capability.GetProperty("per_item_local_confirmation").GetBoolean());
        Assert.False(capability.GetProperty("permanent_policy_supported").GetBoolean());
        Assert.Equal(1000, capability.GetProperty("policy_keep_latest_count_max").GetInt32());
        var legacy = await f.Post("legacy");
        Assert.Equal(HttpStatusCode.Accepted, legacy.Status);
        Assert.False(legacy.Json.GetProperty("data").TryGetProperty("policy", out _));
        var id = legacy.Json.GetProperty("data").GetProperty("batch_deletion_request_id").GetString()!;
        (await f.Prompts.Next()).Decide(LocalRetentionUiChoice.Reject);
        Assert.Null((await f.Finish(id)).Policy);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Post("legacy", Body(f.Ids, Policy(f)))).Status);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM ordinary_deletion_batch_policies;"));
    }

    [Theory]
    [InlineData("DELETE FROM ordinary_deletion_batch_policies;")]
    [InlineData("UPDATE ordinary_deletion_batch_policies SET policy_digest='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';")]
    [InlineData("UPDATE ordinary_deletion_batch_items SET child_key='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' WHERE ordinal=0;")]
    public async Task BrokenParentAtDispatchFailsClosedBeforeChildCreation(string sql)
    {
        using var f = new BatchDeletion388Fixture();
        using var gate = new BatchDeletion388Fixture.Barrier();
        AddKeeper(f);
        f.Batch.CheckpointForTests = (stage, ordinal) =>
        {
            if (stage == "reserved" && ordinal == 0) gate.Block();
        };
        var id = await Create(f, Body(f.Ids, Policy(f)));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try { f.F.Db.Corrupt(sql); }
        finally { gate.Release(); }
        Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await QueryStatus(f, id));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM ordinary_deletion_requests;"));
        Assert.Equal(0, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
        f.PreserveBytes();
    }

    [Theory]
    [InlineData("DELETE FROM ordinary_deletion_request_policies;")]
    [InlineData("DELETE FROM ordinary_deletion_requests;")]
    [InlineData("DELETE FROM ordinary_deletion_operation_policies;")]
    [InlineData("DELETE FROM ordinary_deletion_batch_policy_children;")]
    [InlineData("DELETE FROM ordinary_deletion_batch_items WHERE ordinal=0; DELETE FROM ordinary_deletion_batch_policy_children;")]
    public async Task DamagedChildOrReservationAfterPrepareNeverReachesNativeDisposition(string sql)
    {
        var ledger = new DeletionRequestFixture.Io();
        using var f = new BatchDeletion388Fixture(ledger: ledger);
        using var gate = new BatchDeletion388Fixture.Barrier();
        AddKeeper(f);
        ledger.OnStep = (stage, _, _) => { if (stage == "prepare_after_close") gate.Block(); };
        var id = await Create(f, Body(f.Ids, Policy(f)));
        (await f.Prompts.Next()).Decide(LocalRetentionUiChoice.Approve);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try { f.F.Db.Corrupt(sql); }
        finally { gate.Release(); }
        Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await QueryStatus(f, id));
        Assert.Equal(1, f.Prompts.Begins);
        Assert.Equal(0, f.Native.Calls);
        f.PreserveBytes();
    }

    [Fact]
    public async Task CorruptionAfterAcceptedChildDoesNotBecomeFalseNonExecutionOrDispatchSecond()
    {
        using var f = new BatchDeletion388Fixture();
        using var gate = new BatchDeletion388Fixture.Barrier();
        AddKeeper(f);
        f.Batch.CheckpointForTests = (stage, ordinal) =>
        {
            if (stage == "child_retired" && ordinal == 0) gate.Block();
        };
        var body = Body(f.Ids, Policy(f));
        var id = await Create(f, body);
        (await f.Prompts.Next()).Decide(LocalRetentionUiChoice.Approve);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(6));
        try { f.F.Db.Corrupt("DELETE FROM ordinary_deletion_batch_policies;"); }
        finally { gate.Release(); }
        Assert.True(await f.Batch.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await QueryStatus(f, id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Post("policy-batch", body)).Status);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM ordinary_deletion_operations WHERE phase='settled' AND progress=2;"));
        Assert.Equal(1, f.Native.Calls);
        Assert.Equal(1, f.Prompts.Begins);
    }

    private static async Task<HttpStatusCode> QueryStatus(BatchDeletion388Fixture f, string id)
    {
        using var response = await f.Client.GetAsync(BatchDeletion388Fixture.Endpoint + "/" + id);
        return response.StatusCode;
    }

    [Fact]
    public async Task IncrementalV50PreservesLegacyBatchAndV49BackupWithSinglePolicy()
    {
        using var f = new BatchDeletion388Fixture();
        AddKeeper(f);
        var legacy = await f.Create();
        (await f.Prompts.Next()).Decide(LocalRetentionUiChoice.Reject);
        await f.Finish(legacy);
        var single = await f.Single.RequestAsync(f.Ids[0], "single-policy", policy: Policy(f));
        (await f.Prompts.Next()).Decide(LocalRetentionUiChoice.Approve);
        Assert.True(await f.Single.WaitForIdleAsync());
        var path = f.F.Db.Store.DatabasePath;
        f.F.Db.RawExecute("DROP TABLE ordinary_deletion_host_rejections; DELETE FROM schema_migrations WHERE version=51; DROP TABLE ordinary_deletion_batch_policy_children; DROP TABLE ordinary_deletion_batch_policies; DELETE FROM schema_migrations WHERE version=50;");
        var expected = SqlitePublicationIdentityReferenceTests.OldBytes(path, true, 49);
        var backup = Path.Combine(f.F.Db.Root, "before-v50.db");
        using (var source = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        using (var destination = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await QueryStatus(f, legacy));
        f.F.Db.Store.Initialize();
        f.F.Db.Store.Initialize();
        Assert.Equal(expected, SqlitePublicationIdentityReferenceTests.OldBytes(path, true, 49));
        Assert.Equal(expected, SqlitePublicationIdentityReferenceTests.OldBytes(backup, true, 49));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM schema_migrations WHERE version=50;"));
        Assert.Equal(SqliteSchemaV50.Checksum, SqliteSchemaV1.ComputeChecksum(SqliteSchemaV50.Definition));
        Assert.Null((await f.Finish(legacy)).Policy);
        var replay = await f.Single.RequestAsync(f.Ids[0], "single-policy", policy: Policy(f));
        Assert.Equal(single.DeletionRequestId, replay.DeletionRequestId);
        Assert.True(replay.FileActionPerformed);
        Assert.True(await f.Single.WaitForIdleAsync());
        Assert.Equal(1, f.Native.Calls);
        Assert.Equal(2, f.Prompts.Begins);
    }
}
