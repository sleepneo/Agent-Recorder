using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Xunit;

namespace AgentRecorder.Tests;

public sealed partial class OrdinaryWgcBundleTests
{
    private const long Quota420MiB = 1024L * 1024;
    private static readonly TimeSpan Quota420Wait = TimeSpan.FromSeconds(12);

    [Theory]
    [InlineData(null, 0, null)]
    [InlineData("0", 0, null)]
    [InlineData("1", 1, null)]
    [InlineData("01048576", 1048576, null)]
    [InlineData("", null, "invalid_integer")]
    [InlineData(" 1", null, "invalid_integer")]
    [InlineData("-1", null, "invalid_integer")]
    [InlineData("+1", null, "invalid_integer")]
    [InlineData("２", null, "invalid_integer")]
    [InlineData("1048577", null, "out_of_range")]
    [InlineData("99999999999999999", null, "out_of_range")]
    public void ManagedVideoQuota420Config(string? raw, int? mib, string? error)
    {
        var result = ManagedVideoQuotaSetting.Parse(raw);
        Assert.Equal(mib * Quota420MiB, result.EffectiveBytes);
        Assert.Equal(error, result.InvalidReason);
        Assert.Equal(mib > 0, result.Enabled);
        Assert.Equal(raw is null ? "default" : "environment", result.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    public async Task ManagedVideoQuota420DisabledNeverReadsAtEitherCheck(string? setting)
    {
        using var f = RuntimeFixture();
        await f.Prepare();
        NestedWindow381Providers(f);
        int reads = 0;
        ConfigureQuota420(f, setting, () =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(new ManagedVideoQuotaUsage(null, "failed"));
        });
        f.StartApi();
        var first = await NestedWindow381Accept(f, IndependentWindow382Request(f));
        var second = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
        var a = await IndependentWindow382Approve(f, first);
        var b = await IndependentWindow382Approve(f, second);
        Assert.Null(first.ManagedQuotaAdmission);
        Assert.Null(second.ManagedQuotaAdmission);
        Assert.Equal(0, reads);
        a.Process.Finish.TrySetResult();
        b.Process.Finish.TrySetResult();
        await Until(() => first.IsFinalized && second.IsFinalized);
        await Task.WhenAll(a.Backend.OutputRetirement, b.Backend.OutputRetirement).WaitAsync(Quota420Wait);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ManagedVideoQuota420ExactCapacityAndOneByteShortfall(int delta)
    {
        using var f = RuntimeFixture();
        SeedQuota420(f, 5 * Quota420MiB + delta);
        ConfigureRealQuota420(f, "105");
        f.StartApi();
        var response = await NestedWindow381Post(f, f.Request("window"));
        if (delta == 0)
        {
            Assert.Equal(HttpStatusCode.OK, response.Status);
            Assert.Single(f.Tray.Confirmations);
        }
        else
        {
            var details = Quota420Error(response, HttpStatusCode.Conflict, "MANAGED_VIDEO_QUOTA_EXCEEDED", "estimated_usage_exceeds_quota");
            Assert.Equal(105 * Quota420MiB, details.GetProperty("configured_bytes").GetInt64());
            Assert.Equal(5 * Quota420MiB + 1, details.GetProperty("recorded_bytes").GetInt64());
            Assert.Equal(100 * Quota420MiB, details.GetProperty("estimated_bytes").GetInt64());
            Assert.Equal(1, details.GetProperty("shortfall_bytes").GetInt64());
            Assert.Empty(f.Tray.Confirmations);
        }
        Assert.Equal(0, f.FactoryCalls);
    }

    [Theory]
    [InlineData("invalid", "invalid_integer")]
    [InlineData("unknown", "recorded_usage_incomplete")]
    [InlineData("timeout", "timeout")]
    [InlineData("busy", "busy")]
    [InlineData("limit", "history_limit")]
    [InlineData("corrupt", "data_invalid")]
    public async Task ManagedVideoQuota420UntrustedUsageAndInvalidConfigRefuseOnlyManagedScope(string scenario, string reason)
    {
        using var f = RuntimeFixture();
        switch (scenario)
        {
            case "invalid":
                ConfigureQuota420(f, "-1", () => throw new InvalidOperationException("invalid policy must not read"));
                break;
            case "timeout":
            case "busy":
                ConfigureQuota420(f, "500", () => Task.FromResult(new ManagedVideoQuotaUsage(null, scenario)));
                break;
            default:
                if (scenario == "unknown") SeedQuota420(f, 40, anchor: false);
                if (scenario == "corrupt") Quota420Sql(f, "DROP INDEX idx_ordinary_recording_history_status_created;");
                if (scenario == "limit")
                    Quota420Sql(f, $"""
                        WITH RECURSIVE rows(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM rows WHERE i<{ManagedVideoUsage.HistoryLimit + 1})
                        INSERT INTO ordinary_recording_history(recording_id,history_sequence,created_at_utc,status_code,source_type,capture_semantics,
                            source_title,audio_mode,output_path,completed_at_utc,updated_at_utc)
                        SELECT 'quota-bulk-'||i,i,0,'completed','display','display_capture','synthetic','none',
                            'C:\other\external.mp4',10,10 FROM rows;
                        """);
                ConfigureRealQuota420(f, "500");
                break;
        }
        f.StartApi();
        var result = await NestedWindow381Post(f, f.Request("window"));
        var details = Quota420Error(result, HttpStatusCode.ServiceUnavailable,
            scenario == "invalid" ? "MANAGED_VIDEO_QUOTA_CONFIGURATION_INVALID" : "MANAGED_VIDEO_QUOTA_USAGE_UNAVAILABLE", reason);
        Assert.False(details.TryGetProperty("recorded_bytes", out _));
        Assert.Empty(f.Tray.Confirmations);
        var external = f.Request("window");
        external["output"]!["directory"] = Path.Combine(f.Root, "external");
        Assert.Equal(HttpStatusCode.OK, (await NestedWindow381Post(f, external)).Status);
        Assert.Single(f.Tray.Confirmations);
        Assert.Equal(0, f.FactoryCalls);
    }

    [Theory]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    public async Task ManagedVideoQuota420ApprovalRechecksOnlyFinalDirectory(bool startsExternal, bool endsExternal, int expectedReads)
    {
        using var f = RuntimeFixture();
        await f.Prepare();
        var external = Path.Combine(f.Root, "external");
        var managed = Path.Combine(f.Root, "Videos");
        long usage = 0;
        int reads = 0;
        ConfigureQuota420(f, "100", () =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(new ManagedVideoQuotaUsage(Interlocked.Read(ref usage)));
        });
        f.StartApi();
        var request = f.Request("window");
        request["output"]!["directory"] = startsExternal ? external : managed;
        var rec = await NestedWindow381Accept(f, request);
        Interlocked.Exchange(ref usage, 1);
        f.Tray.Confirmations[rec.Id](ConfirmationDecision.Approve(endsExternal ? external : managed));
        if (endsExternal)
        {
            await Until(() => rec.State == RecState.recording || rec.IsFinalized);
            Assert.Equal(RecState.recording, rec.State);
            Assert.Equal(1, f.FactoryCalls);
            Assert.Null(rec.ManagedQuotaAdmission);
            await f.Terminal(rec, manual: false);
        }
        else
        {
            Assert.True(rec.IsFinalized);
            Assert.Equal("MANAGED_VIDEO_QUOTA_EXCEEDED", rec.StopReason);
            Assert.Equal(0, f.FactoryCalls);
            using var client = f.Client();
            var status = await Get(client, "/api/v1/recordings/" + rec.Id);
            var failure = status.GetProperty("managed_video_quota_failure");
            Assert.Equal("MANAGED_VIDEO_QUOTA_EXCEEDED", failure.GetProperty("code").GetString());
            Assert.Equal("before_start", failure.GetProperty("details").GetProperty("stage").GetString());
            Assert.Equal(1, failure.GetProperty("details").GetProperty("shortfall_bytes").GetInt64());
            var waited = await Get(client, "/api/v1/recordings/" + rec.Id + "?since_status=recording&wait_ms=1");
            Assert.Equal(failure.GetRawText(), waited.GetProperty("managed_video_quota_failure").GetRawText());
        }
        Assert.Equal(expectedReads, reads);
    }

    [Fact]
    public async Task ManagedVideoQuota420TwoPendingApprovalsCannotCrossOneOwner()
    {
        using var f = RuntimeFixture();
        await f.Prepare();
        NestedWindow381Providers(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        ConfigureQuota420(f, "500", async () =>
        {
            if (Interlocked.Increment(ref reads) == 3)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Quota420Wait);
            }
            return new(0);
        });
        f.StartApi();
        var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
        var b = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
        var approval = Task.Run(() => f.Tray.Confirmations[a.Id](ConfirmationDecision.Approve()));
        try
        {
            await entered.Task.WaitAsync(Quota420Wait);
            f.Tray.Confirmations[b.Id](ConfirmationDecision.Approve());
            Assert.Equal("MANAGED_VIDEO_QUOTA_BUSY", b.StopReason);
            Assert.Equal(0, f.FactoryCalls);
            Assert.Equal(3, reads);
        }
        finally { release.TrySetResult(); await approval.WaitAsync(Quota420Wait); }
        await Until(() => a.State == RecState.recording || a.IsFinalized);
        Assert.Equal(RecState.recording, a.State);
        f.Tray.Confirmations[b.Id](ConfirmationDecision.Approve());
        Assert.Equal(1, f.FactoryCalls);
        await f.Terminal(a, manual: false);
        await f.Wgc!.OutputRetirement.WaitAsync(Quota420Wait);
    }

    [Fact]
    public async Task ManagedVideoQuota420CancellationDuringReadAndLateCallbackCannotStart()
    {
        using var f = RuntimeFixture();
        await f.Prepare();
        NestedWindow381Providers(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        ConfigureQuota420(f, "500", async () =>
        {
            if (Interlocked.Increment(ref reads) == 3)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Quota420Wait);
            }
            return new(0);
        });
        f.StartApi();
        var a = await NestedWindow381Accept(f, IndependentWindow382Request(f));
        var b = await NestedWindow381Accept(f, IndependentWindow382Request(f, "window_43"));
        var callback = f.Tray.Confirmations[a.Id];
        var approval = Task.Run(() => callback(ConfirmationDecision.Approve()));
        try
        {
            await entered.Task.WaitAsync(Quota420Wait);
            await Task.Run(() => f.Engine.Stop(a.Id, "user_requested")).WaitAsync(Quota420Wait);
            Assert.Equal(RecState.cancelled, a.State);
            Assert.Equal(0, f.FactoryCalls);
        }
        finally { release.TrySetResult(); await approval.WaitAsync(Quota420Wait); }
        var run = await IndependentWindow382Approve(f, b);
        callback(ConfirmationDecision.Approve());
        Assert.Equal(1, f.FactoryCalls);
        await f.Terminal(b, manual: false);
        await run.Backend.OutputRetirement.WaitAsync(Quota420Wait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedVideoQuota420PublicationWaitsForActualAnchorCommitAndCountsItsActualBytes(bool failCommit)
    {
        using var f = RuntimeFixture();
        await f.Prepare();
        AppendQuota420FreeBox(f.Clip, (int)(100 * Quota420MiB + 1));
        var actual = new FileInfo(f.Clip).Length;
        SeedQuota420(f, Quota420MiB);
        ConfigureRealQuota420(f, "101");
        NestedWindow381Providers(f);
        f.StartApi();
        var rec = await NestedWindow381Accept(f, IndependentWindow382Request(f));
        var run = await IndependentWindow382Approve(f, rec);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Writer.BeforePersistForTests = snapshot =>
        {
            if (snapshot.RecordingId != rec.Id || snapshot.Status != "completed") return;
            entered.TrySetResult();
            if (!release.Wait(Quota420Wait)) throw new TimeoutException("owned completion gate");
            if (failCommit) throw new IOException("owned commit failure");
        };
        f.Writer.AfterPersistForTests = snapshot =>
        {
            if (snapshot.RecordingId == rec.Id && snapshot.Status == "completed") persisted.TrySetResult();
        };
        try
        {
            run.Process.Finish.TrySetResult();
            await entered.Task.WaitAsync(Quota420Wait);
            Assert.True(rec.IsFinalized);
            Assert.Equal(actual, new FileInfo(rec.OutputPath).Length);
            await run.Backend.OutputRetirement.WaitAsync(Quota420Wait);
            var response = await NestedWindow381Post(f, IndependentWindow382Request(f, "window_43"));
            Quota420Error(response, HttpStatusCode.ServiceUnavailable, "MANAGED_VIDEO_QUOTA_USAGE_UNAVAILABLE", "previous_run_unsettled");
            Assert.Equal(1, f.FactoryCalls);
        }
        finally { release.Set(); await persisted.Task.WaitAsync(Quota420Wait); }
        var next = await NestedWindow381Post(f, IndependentWindow382Request(f, "window_43"));
        if (failCommit)
        {
            try { Quota420Error(next, HttpStatusCode.ServiceUnavailable, "MANAGED_VIDEO_QUOTA_USAGE_UNAVAILABLE", "previous_run_unsettled"); }
            finally
            {
                // Repair only this owned fixture so its normal disposal can verify a clean drain.
                f.Writer.BeforePersistForTests = null;
                rec.OrdinaryHistoryPublication!(rec);
                Assert.Equal("completed", f.Writer.Read(rec.Id)!.Status);
            }
        }
        else
        {
            Assert.Equal("completed", f.Writer.Read(rec.Id)!.Status);
            var details = Quota420Error(next, HttpStatusCode.Conflict, "MANAGED_VIDEO_QUOTA_EXCEEDED", "estimated_usage_exceeds_quota");
            Assert.Equal(actual + Quota420MiB, details.GetProperty("recorded_bytes").GetInt64());
        }
        Assert.True(File.Exists(rec.OutputPath));
        Assert.Equal(1, f.FactoryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedVideoQuota420FailedRunReleasesOnlyWithNoPublicationAndRetiredOutput(bool attempted)
    {
        using var f = RuntimeFixture();
        await f.Prepare();
        NestedWindow381Providers(f);
        ConfigureRealQuota420(f, "500");
        f.StartApi();
        var rec = await NestedWindow381Accept(f, IndependentWindow382Request(f));
        var run = await IndependentWindow382Approve(f, rec);
        if (attempted)
            Assert.Throws<IOException>(() => rec.Config.StorageSafety!.TryCommit(() => throw new IOException("ambiguous rename"), true));
        run.Process.FailMedia = true;
        run.Process.Finish.TrySetResult();
        await Until(() => rec.IsFinalized);
        await run.Backend.OutputRetirement.WaitAsync(Quota420Wait);
        Assert.Equal("failed", f.Writer.Read(rec.Id)!.Status);
        Assert.False(File.Exists(rec.OutputPath));

        var next = await NestedWindow381Post(f, IndependentWindow382Request(f, "window_43"));
        if (attempted)
            Quota420Error(next, HttpStatusCode.ServiceUnavailable, "MANAGED_VIDEO_QUOTA_USAGE_UNAVAILABLE", "previous_run_unsettled");
        else
            Assert.True(next.Status == HttpStatusCode.OK, next.Body);
        Assert.Equal(1, f.FactoryCalls);
    }

    [Fact]
    public void ManagedVideoQuota420LateCommitForPriorRunCannotReleaseTheNewOwner()
    {
        using var f = RuntimeFixture();
        var quota = new ManagedVideoQuota(ManagedVideoQuotaSetting.Parse("500"), f.Root,
            () => throw new InvalidOperationException("no usage query in this witness test"));
        var path = Path.Combine(f.Root, "Videos", "owned.mp4");
        var first = new Recording("owned-first") { OutputPath = path };
        var firstOwner = new ManagedVideoQuota.Admission(first);
        var nextOwner = new ManagedVideoQuota.Admission(new Recording("owned-next") { OutputPath = path });
        var at = DateTimeOffset.UtcNow;
        var committed = new OrdinaryRecordingHistorySnapshot(first.Id, at, "completed", "window", "window_surface",
            "synthetic", 1, "none", null, null, null, null, path, null, null, null, null, null, null,
            at, at.AddSeconds(1), "duration_reached", 1024, 1)
        {
            PublicationReceipt = PublicationReceipt.IssueForTests(path, 1024, new(1, 2, 3))
        };
        Assert.True(quota.TryOwn(firstOwner));
        quota.Acknowledge(firstOwner, committed);
        Assert.True(quota.TryOwn(nextOwner));
        quota.Acknowledge(firstOwner, committed);
        Assert.False(quota.TryOwn(new ManagedVideoQuota.Admission(first)));
        Assert.Equal("MANAGED_VIDEO_QUOTA_BUSY", quota.CheckAvailability(_ => false, true)!.Code);
    }

    private static void ConfigureQuota420(Fixture f, string? value, Func<Task<ManagedVideoQuotaUsage>> read) =>
        f.Engine.ConfigureManagedVideoQuota(new(ManagedVideoQuotaSetting.Parse(value), f.Root, read));

    private static void ConfigureRealQuota420(Fixture f, string value)
    {
        var key = ManagedVideoQuotaSetting.EnvironmentVariable;
        var before = Environment.GetEnvironmentVariable(key);
        try { Environment.SetEnvironmentVariable(key, value); SqliteManagedVideoQuota.Configure(f.Engine, f.Store, f.Root); }
        finally { Environment.SetEnvironmentVariable(key, before); }
    }

    private static void SeedQuota420(Fixture f, long bytes, bool anchor = true)
    {
        var at = DateTimeOffset.UtcNow.AddHours(-1);
        var path = Path.Combine(f.Root, "Videos", "historical.mp4");
        f.Writer.Register(new OrdinaryRecordingHistorySnapshot("quota-historical", at, "completed", "display", "display_capture",
            "synthetic", 1, "none", null, null, null, null, path, null, null, null, null, null, null,
            at, at.AddSeconds(1), "duration_reached", bytes, 1)
        {
            PublicationReceipt = anchor ? PublicationReceipt.IssueForTests(path, bytes, new(3, 6, 9)) : null
        });
    }

    private static void Quota420Sql(Fixture f, string sql)
    {
        using var connection = f.Store.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static JsonElement Quota420Error((HttpStatusCode Status, string Body) response, HttpStatusCode status, string code, string reason)
    {
        Assert.True(status == response.Status, response.Body);
        using var json = JsonDocument.Parse(response.Body);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        var details = error.GetProperty("details");
        Assert.Equal(reason, details.GetProperty("reason_code").GetString());
        return details.Clone();
    }

    private static void AppendQuota420FreeBox(string path, int size)
    {
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        "free"u8.CopyTo(header[4..]);
        file.Write(header);
        file.SetLength(file.Length + size - header.Length);
    }
}
