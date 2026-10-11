using System.Globalization;
using System.Collections.Concurrent;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public sealed class OrdinaryBundleRecoveryTransactionTests(ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        private readonly string _base = Path.GetFullPath(Path.Combine(TestHelper.ProjectRoot, ".local-data/temp/task329r"));
        internal readonly string Root;
        internal readonly SqliteOperationalStore Store;
        internal SqliteOrdinaryRecordingHistoryStore Writer;
        internal readonly OrdinaryRecordingHistorySnapshot Row;
        internal readonly OrdinaryRecordingBundleOperation Operation;

        internal readonly ConcurrentQueue<string> Notifications = new();

        internal Fixture(bool seedGenerating = true)
        {
            Root = Path.Combine(_base, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new(Path.Combine(Root, "owned.db"));
            Store.Initialize();
            var at = DateTimeOffset.UtcNow.AddMinutes(-1);
            Row = PublicationAnchorTestFixture.Anchored(new("old", at, "completed", "display", "display_capture",
                "Synthetic, no capture", 1, "none", null, null, null, null, Path.Combine(Root, "no-media.mp4"),
                null, null, null, null, null, null, at.AddSeconds(1), at.AddSeconds(2), "duration_reached", 100, 1));
            Operation = OrdinaryRecordingBundleOperation.Create(Row.RecordingId, Row.OutputPath);
            using (var previous = new SqliteOrdinaryRecordingHistoryStore(Store))
            {
                previous.Register(Row with { BundleRegistration = new(seedGenerating ? Operation : null) });
                Assert.True(previous.DrainAndDispose(TimeSpan.FromSeconds(3)));
            }
            Writer = new(Store, (_, code) => Notifications.Enqueue(code));
        }

        internal void Execute(string sql)
        {
            using var c = Store.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        internal string Scalar(string sql)
        {
            using var c = Store.OpenReadOnlyConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)!;
        }

        internal string State(string id = "old") => Scalar($"SELECT status_code FROM ordinary_recording_bundles WHERE recording_id='{id}';");
        internal string Binding() => Scalar("SELECT COALESCE(operation_id,'revoked') FROM ordinary_recording_bundles WHERE recording_id='old';");
        internal string MediaAndAnchors()
        {
            using var c = Store.OpenReadOnlyConnection();
            var parts = new List<string>();
            foreach (var sql in new[] { "SELECT * FROM ordinary_recording_history ORDER BY recording_id;", "SELECT * FROM ordinary_publication_anchors ORDER BY recording_id;" })
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                while (r.Read()) parts.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i =>
                    r.GetValue(i) is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
            }
            return string.Join("\n", parts);
        }

        internal (OrdinaryRecordingHistorySnapshot Row, OrdinaryRecordingBundleOperation Operation) Live()
        {
            var row = PublicationAnchorTestFixture.Anchored(Row with { RecordingId = "live", OutputPath = Path.Combine(Root, "live.mp4"), PublicationReceipt = null });
            var operation = OrdinaryRecordingBundleOperation.Create(row.RecordingId, row.OutputPath);
            return (row with { BundleRegistration = new(operation) }, operation);
        }

        internal static OrdinaryRecordingBundleUpdate Ready(OrdinaryRecordingBundleOperation operation) =>
            new(operation, new("ready", operation.BundlePath, null));

        public void Dispose()
        {
            Writer.Dispose();
            SqliteConnection.ClearAllPools();
            Assert.Equal(_base, Path.GetDirectoryName(Root));
            Directory.Delete(Root, true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void SqlRollbackRetriesOnSameWriterRevokeOldOperationAndPreserveMedia(int failures)
    {
        using var f = new Fixture();
        var before = f.MediaAndAnchors();
        f.Execute("CREATE TRIGGER recovery_fault BEFORE UPDATE ON ordinary_recording_bundles BEGIN SELECT RAISE(ABORT,'injected recovery SQL failure'); END;");
        for (var i = 0; i < failures; i++)
        {
            var error = Record.Exception(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow));
            output.WriteLine($"SQL recovery attempt={i + 1}; exception={error?.GetType().Name ?? "none"}; durable_state={f.State()}");
            var sqlite = Assert.IsType<SqliteException>(error);
            Assert.Equal(19, sqlite.SqliteErrorCode);
            Assert.Equal("generating", f.State());
            Assert.Equal(f.Operation.OperationId, f.Binding());
            Assert.Equal(before, f.MediaAndAnchors());
        }
        f.Execute("DROP TRIGGER recovery_fault;");
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);
        var retry = f.State();
        var queued = f.Writer.TryPublishBundle(Fixture.Ready(f.Operation));
        var late = f.Writer.Read(f.Row.RecordingId)!.Bundle!.Status;
        output.WriteLine($"RetryRecovery=returned; durable_state={retry}; OldOperationQueueAccepted={queued}; after_late_result={late}");
        Assert.Equal("interrupted_unknown", retry);
        Assert.True(queued); // Queue admission does not prove the result was accepted by storage.
        Assert.Equal("interrupted_unknown", late);
        Assert.Equal("revoked", f.Binding());
        Assert.Equal(before, f.MediaAndAnchors());
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(late, f.State());
        Assert.Equal(before, f.MediaAndAnchors());
        Assert.False(File.Exists(f.Row.OutputPath));
        Assert.False(Directory.Exists(f.Operation.BundlePath));
        Assert.True(f.Writer.DrainAndDispose(TimeSpan.FromSeconds(3)));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void PreCommitInjectionRollsBackAndRemainsRetryable(int failures)
    {
        using var f = new Fixture();
        var before = f.MediaAndAnchors();
        var calls = 0;
        f.Writer.BeforeRecoveryCommitForTests = (_, _) =>
        {
            calls++;
            throw new IOException("injected immediately before Commit, not a disk fault");
        };
        for (var i = 0; i < failures; i++)
        {
            Assert.Throws<IOException>(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow));
            Assert.Equal("generating", f.State());
            Assert.Equal(f.Operation.OperationId, f.Binding());
            Assert.Equal(before, f.MediaAndAnchors());
        }
        Assert.Equal(failures, calls);
        f.Writer.BeforeRecoveryCommitForTests = null;
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);
        Assert.True(f.Writer.TryPublishBundle(Fixture.Ready(f.Operation)));
        Assert.Equal("interrupted_unknown", f.Writer.Read(f.Row.RecordingId)!.Bundle!.Status);
        Assert.Equal("revoked", f.Binding());
        Assert.Equal(before, f.MediaAndAnchors());
        output.WriteLine($"Pre-Commit injection count={calls}; retry and late result=interrupted_unknown; media/anchor unchanged.");
    }

    [Fact]
    public void ActualSqliteCommitFailureFromDeferredForeignKeyDoesNotConsumeRecovery()
    {
        using var f = new Fixture();
        var before = f.MediaAndAnchors();
        var seamReturned = false;
        f.Writer.BeforeRecoveryCommitForTests = (connection, transaction) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "PRAGMA defer_foreign_keys=ON; INSERT INTO ordinary_recording_bundles VALUES('commit-orphan',$path,NULL,'not_applicable',NULL,NULL);";
            cmd.Parameters.AddWithValue("$path", Path.Combine(f.Root, "orphan.mp4"));
            cmd.ExecuteNonQuery();
            seamReturned = true; // The INSERT returned; the real Commit must reject its deferred FK.
        };
        var error = Assert.Throws<SqliteException>(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow));
        Assert.True(seamReturned);
        Assert.Equal(787, error.SqliteExtendedErrorCode);
        Assert.Equal("generating", f.State());
        Assert.Equal(f.Operation.OperationId, f.Binding());
        Assert.Equal("0", f.Scalar("SELECT COUNT(*) FROM ordinary_recording_bundles WHERE recording_id='commit-orphan';"));
        Assert.Equal(before, f.MediaAndAnchors());
        f.Writer.BeforeRecoveryCommitForTests = null;
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);
        Assert.True(f.Writer.TryPublishBundle(Fixture.Ready(f.Operation)));
        Assert.Equal("interrupted_unknown", f.Writer.Read(f.Row.RecordingId)!.Bundle!.Status);
        Assert.Equal("revoked", f.Binding());
        Assert.Equal(before, f.MediaAndAnchors());
        output.WriteLine($"Real SQLite Commit error={error.SqliteExtendedErrorCode}; injected deferred FK, no disk fault; retry/late result=interrupted_unknown.");
    }

    [Theory]
    [InlineData("register")]
    [InlineData("snapshot")]
    [InlineData("bundle")]
    public async Task RecoveryFirstSerializesProducerAdmissionAndRejectsBusyWorkWithinBound(string producer)
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Writer.BeforeRecoveryCommitForTests = (_, _) =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var recovering = Task.Run(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var (live, operation) = f.Live();
        try
        {
            await Task.Run(() =>
            {
                if (producer == "register")
                    Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Register(live));
                else if (producer == "snapshot") Assert.False(f.Writer.TryPublish(live));
                else Assert.False(f.Writer.TryPublishBundle(Fixture.Ready(operation)));
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("0", f.Scalar("SELECT COUNT(*) FROM ordinary_recording_history WHERE recording_id='live';"));
            Assert.Equal(0, f.Writer.UnsettledWriteFailureCountForTests);
            if (producer != "register") Assert.Contains("history_startup_recovery_busy", f.Notifications);
        }
        finally { release.Set(); }
        await recovering.WaitAsync(TimeSpan.FromSeconds(3));
        f.Writer.BeforeRecoveryCommitForTests = null;
        Assert.Equal("interrupted_unknown", f.Writer.Read(f.Row.RecordingId)!.Bundle!.Status);
        f.Writer.Register(live);
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);
        Assert.Equal("generating", f.Writer.Read(live.RecordingId)!.Bundle!.Status);
        Assert.True(f.Writer.TryPublishBundle(Fixture.Ready(operation)));
        Assert.Equal("ready", f.Writer.Read(live.RecordingId)!.Bundle!.Status);
    }

    [Fact]
    public async Task FailedRecoveryKeepsStartupPendingAndProducerCannotCloseRetryEligibility()
    {
        using var f = new Fixture();
        var before = f.MediaAndAnchors();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Writer.BeforeRecoveryCommitForTests = (_, _) =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            throw new IOException("injected rollback at producer boundary");
        };
        var recovering = Task.Run(() => Record.Exception(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.False(await Task.Run(() => f.Writer.TryPublishBundle(Fixture.Ready(f.Operation))).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); }
        Assert.IsType<IOException>(await recovering.WaitAsync(TimeSpan.FromSeconds(3)));
        var (live, operation) = f.Live();
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => f.Writer.Register(live));
        Assert.False(f.Writer.TryPublish(live));
        Assert.False(f.Writer.TryPublishBundle(Fixture.Ready(operation)));
        Assert.Equal("generating", f.State());
        Assert.Equal(before, f.MediaAndAnchors());
        Assert.Equal(0, f.Writer.UnsettledWriteFailureCountForTests);
        f.Writer.BeforeRecoveryCommitForTests = null;
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);
        Assert.Equal("interrupted_unknown", f.Writer.Read(f.Row.RecordingId)!.Bundle!.Status);
        Assert.True(f.Writer.TryPublishBundle(Fixture.Ready(f.Operation)));
        Assert.Equal("interrupted_unknown", f.Writer.Read(f.Row.RecordingId)!.Bundle!.Status);
        f.Writer.Register(live);
        Assert.True(f.Writer.TryPublishBundle(Fixture.Ready(operation)));
        Assert.Equal("ready", f.Writer.Read(live.RecordingId)!.Bundle!.Status);
    }

    [Fact]
    public async Task ProducerFirstKeepsLiveGeneratingAndCallbacksDoNotWaitForLaterRecovery()
    {
        using var f = new Fixture(seedGenerating: false);
        var (live, operation) = f.Live();
        f.Writer.Register(live); // Producer phase wins before any recovery attempt.
        var before = f.MediaAndAnchors();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Writer.BeforeRecoveryCommitForTests = (_, _) =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var recovering = Task.Run(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True(await Task.Run(() => f.Writer.TryPublishBundle(Fixture.Ready(operation))).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("generating", f.State(live.RecordingId));
        }
        finally { release.Set(); }
        await recovering.WaitAsync(TimeSpan.FromSeconds(3));
        f.Writer.BeforeRecoveryCommitForTests = null;
        Assert.Equal("ready", f.Writer.Read(live.RecordingId)!.Bundle!.Status);
        Assert.Equal(before, f.MediaAndAnchors());
    }

    [Fact]
    public async Task ConcurrentRecoveryHasBoundedAdmissionAndSuccessfulRepeatIsIdempotent()
    {
        using var f = new Fixture();
        var before = f.MediaAndAnchors();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Writer.BeforeRecoveryCommitForTests = (_, _) =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var first = Task.Run(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var second = await Task.Run(() => Record.Exception(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow))).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsType<OrdinaryRecordingHistoryUnavailableException>(second);
        }
        finally { release.Set(); }
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        f.Writer.BeforeRecoveryCommitForTests = null;
        var recovered = f.Writer.Read(f.Row.RecordingId)!;
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(recovered, f.Writer.Read(f.Row.RecordingId));
        Assert.Equal(before, f.MediaAndAnchors());
        Assert.Equal("revoked", f.Binding());
    }

    [Fact]
    public void FailedRepeatAndInvalidProducerCannotReopenSuccessfullyClosedRecovery()
    {
        using var f = new Fixture();
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow);
        var (live, operation) = f.Live();
        f.Writer.Register(live);
        var before = f.MediaAndAnchors();
        f.Writer.BeforeRecoveryCommitForTests = (_, _) => throw new IOException("failed repeat after successful startup");
        Assert.Throws<IOException>(() => f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => f.Writer.Register(live with { RecordingId = "" }));
        Assert.False(f.Writer.TryPublishBundle(Fixture.Ready(operation) with { Result = new("ready", Path.Combine(f.Root, "wrong.bundle"), null) }));
        f.Writer.BeforeRecoveryCommitForTests = null;
        f.Writer.RecoverIncomplete(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("generating", f.Writer.Read(live.RecordingId)!.Bundle!.Status);
        Assert.Equal("interrupted_unknown", f.State());
        Assert.Equal(before, f.MediaAndAnchors());
        Assert.True(f.Writer.TryPublishBundle(Fixture.Ready(operation)));
        Assert.Equal("ready", f.Writer.Read(live.RecordingId)!.Bundle!.Status);
    }

}
