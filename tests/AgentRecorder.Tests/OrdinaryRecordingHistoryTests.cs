using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using System.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentRecorder.Tests;

public sealed class OrdinaryRecordingHistoryTests
{
    [Fact]
    public void RegistrationIsDurableAndIdempotentAndPagesExcludeLaterInsertions()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database.Store);
        var created = new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero);
        var olderId = Snapshot("rec_a", created);
        var newerId = Snapshot("rec_b", created.AddMinutes(1));
        history.Register(olderId);
        history.Register(newerId);
        history.Register(olderId);

        var first = history.ReadPage(1, null);
        Assert.Equal("rec_b", Assert.Single(first.Items).RecordingId);
        Assert.NotNull(first.NextCursor);

        history.Register(Snapshot("rec_c", created.AddMinutes(2)));
        var second = history.ReadPage(1, first.NextCursor);
        Assert.Equal("rec_a", Assert.Single(second.Items).RecordingId);
        Assert.Null(second.NextCursor);
        Assert.Equal("preparing", history.Read("rec_a")!.Status);
    }

    [Fact]
    public void NonterminalSameStatusAllowsConfirmedOutputPathUpdate()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database.Store);
        var accepted = Snapshot("rec_path", new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero));
        history.Register(accepted);

        Assert.True(history.TryPublish(accepted with { OutputPath = "D:\\approved\\recording.mp4" }));

        var actual = history.Read("rec_path")!;
        Assert.Equal("preparing", actual.Status);
        Assert.Equal("D:\\approved\\recording.mp4", actual.OutputPath);
    }

    [Fact]
    public void WorkerWriteFailureIsAuditedAndReadBarrierReturnsUnavailableUntilRepaired()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        var failures = new List<(string Id, string Error)>();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database.Store,
            (id, error) => failures.Add((id, error)));
        var accepted = Snapshot("rec_worker_failure", new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero));
        history.Register(accepted);
        using (var connection = database.Store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_history_update BEFORE UPDATE ON ordinary_recording_history
                BEGIN SELECT RAISE(ABORT, 'injected history worker failure'); END;
                """;
            command.ExecuteNonQuery();
        }

        Assert.True(history.TryPublish(accepted with { Status = "recording" }));
        var unavailable = Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => history.Read(accepted.RecordingId));
        Assert.Contains("unsettled write failure", unavailable.Message);
        Assert.Contains(failures, failure => failure.Id == accepted.RecordingId && failure.Error == "SqliteException");

        using (var connection = database.Store.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER reject_history_update;";
            command.ExecuteNonQuery();
        }
        Assert.True(history.TryPublish(accepted with { Status = "recording" }));
        Assert.Equal("recording", history.Read(accepted.RecordingId)!.Status);
    }

    [Fact]
    public void QueueFullTerminalFailureCannotBeClearedByOlderQueuedSuccess()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database.Store);
        var created = new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero);
        var accepted = Snapshot("rec_queue_fault", created);
        var unrelated = Snapshot("rec_unrelated", created.AddMinutes(1));
        history.Register(accepted);
        history.Register(unrelated);
        var terminal = accepted with
        {
            Status = "completed",
            StartedAtUtc = created.AddSeconds(1),
            CompletedAtUtc = created.AddSeconds(30),
            ReasonCode = "duration_reached",
            OutputPath = "C:\\captures\\final.mp4",
            OutputSizeBytes = 4096,
            OutputDurationSeconds = 29
        };

        using var writerEntered = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var oldWritesCompleted = new CountdownEvent(129);
        history.BeforePersistForTests = _ =>
        {
            writerEntered.Set();
            if (!releaseWriter.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Timed out waiting for the deterministic history writer gate.");
        };
        history.AfterPersistForTests = _ => oldWritesCompleted.Signal();

        using (var lockingConnection = database.Store.OpenConnection())
        using (var transaction = lockingConnection.BeginTransaction(IsolationLevel.Serializable, deferred: false))
        {
            Assert.True(history.TryPublish(accepted with { OutputPath = "C:\\captures\\older-1.mp4" }));
            Assert.True(writerEntered.Wait(TimeSpan.FromSeconds(5)), "Writer did not reach its bounded test gate.");
            for (var index = 0; index < 128; index++)
                Assert.True(history.TryPublish(accepted with { OutputPath = $"C:\\captures\\older-{index + 2}.mp4" }));
            Assert.False(history.TryPublish(terminal));
            Assert.Equal(1, history.UnsettledWriteFailureCountForTests);
            transaction.Commit();
        }
        releaseWriter.Set();
        history.BeforePersistForTests = null;
        Assert.True(oldWritesCompleted.Wait(TimeSpan.FromSeconds(20)), "Old queued history snapshots did not drain.");
        history.AfterPersistForTests = null;

        OrdinaryRecordingHistoryUnavailableException? unsettled = null;
        OrdinaryRecordingHistorySnapshot? stale = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                stale = history.Read(accepted.RecordingId);
                break;
            }
            catch (OrdinaryRecordingHistoryUnavailableException exception)
            {
                if (exception.Message.Contains("unsettled write failure", StringComparison.Ordinal))
                {
                    unsettled = exception;
                    break;
                }
            }
        }
        Assert.True(unsettled is not null, $"Failure marker count={history.UnsettledWriteFailureCountForTests}; stale status={stale?.Status ?? "null"}.");
        Assert.Null(stale);

        // A successful write for another recording cannot clear this fault.
        Assert.True(history.TryPublish(unrelated with { Status = "recording" }));
        Assert.Throws<OrdinaryRecordingHistoryUnavailableException>(() => history.Read(unrelated.RecordingId));

        // A later exact resend is persisted and is the only operation that can
        // prove the dropped terminal obligation has been repaired.
        Assert.True(history.TryPublish(terminal));
        OrdinaryRecordingHistorySnapshot? repaired = null;
        for (var attempt = 0; attempt < 12 && repaired is null; attempt++)
        {
            try { repaired = history.Read(accepted.RecordingId); }
            catch (OrdinaryRecordingHistoryUnavailableException exception)
            {
                if (exception.Message.Contains("unsettled write failure", StringComparison.Ordinal)) throw;
            }
        }
        Assert.NotNull(repaired);
        Assert.Equal("completed", repaired!.Status);
        Assert.Equal(terminal.OutputPath, repaired.OutputPath);
        Assert.Equal(terminal.CompletedAtUtc, repaired.CompletedAtUtc);
        Assert.Equal("recording", history.Read(unrelated.RecordingId)!.Status);
    }

    [Fact]
    public void EqualTimestampsUseStableRecordingIdTieBreak()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database.Store);
        var created = new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero);
        history.Register(Snapshot("rec_z", created));
        history.Register(Snapshot("rec_a", created));

        var page = history.ReadPage(1, null);
        Assert.Equal("rec_a", Assert.Single(page.Items).RecordingId);
        Assert.Equal("rec_z", Assert.Single(history.ReadPage(1, page.NextCursor).Items).RecordingId);
    }

    [Fact]
    public void TerminalPublicationWinsAgainstLateActiveSnapshotAndRestartRecoveryIsExplicit()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using (var history = new SqliteOrdinaryRecordingHistoryStore(database.Store))
        {
            var initial = Snapshot("rec_terminal", new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero));
            history.Register(initial);
            var completed = initial with
            {
                Status = "completed",
                StartedAtUtc = initial.CreatedAtUtc.AddSeconds(1),
                CompletedAtUtc = initial.CreatedAtUtc.AddSeconds(10),
                OutputPath = "C:\\captures\\actual-final.mp4",
                OutputSizeBytes = 42,
                OutputDurationSeconds = 9.5
            };
            Assert.True(history.TryPublish(completed));
            Assert.True(history.TryPublish(completed with { Status = "recording", CompletedAtUtc = null }));
            var read = history.Read("rec_terminal")!;
            Assert.Equal("completed", read.Status);
            Assert.Equal("C:\\captures\\actual-final.mp4", read.OutputPath);
            Assert.Equal(42, read.OutputSizeBytes);
        }

        using (var restarted = new SqliteOrdinaryRecordingHistoryStore(database.Store))
        {
            restarted.Register(Snapshot("rec_active", new DateTimeOffset(2026, 10, 5, 2, 0, 0, TimeSpan.Zero)));
            restarted.TryPublish(Snapshot("rec_active", new DateTimeOffset(2026, 10, 5, 2, 0, 0, TimeSpan.Zero)) with { Status = "recording" });
            restarted.RecoverIncomplete(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero));
            Assert.Equal("interrupted_unknown", restarted.Read("rec_active")!.Status);
            Assert.Equal("completed", restarted.Read("rec_terminal")!.Status);
        }
    }

    [Fact]
    public void TerminalWinnerRejectsChangedPayloadEvenWhenStatusIsIdentical()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database.Store);
        var initial = Snapshot("rec_winner", new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero));
        history.Register(initial);
        var winner = initial with
        {
            Status = "completed",
            OutputPath = "C:\\captures\\winner.mp4",
            StartedAtUtc = initial.CreatedAtUtc.AddSeconds(1),
            CompletedAtUtc = initial.CreatedAtUtc.AddSeconds(12),
            ReasonCode = "duration_reached",
            OutputSizeBytes = 4096,
            OutputDurationSeconds = 10.75
        };

        Assert.True(history.TryPublish(winner));
        Assert.True(history.TryPublish(winner)); // exact replay is safe
        Assert.True(history.TryPublish(winner with
        {
            OutputPath = "C:\\captures\\late.mp4",
            CompletedAtUtc = initial.CreatedAtUtc.AddSeconds(40),
            ReasonCode = "late_callback",
            OutputSizeBytes = 9,
            OutputDurationSeconds = 1
        }));
        Assert.True(history.TryPublish(winner with { Status = "failed", ReasonCode = "late_failure" }));

        var actual = history.Read("rec_winner")!;
        Assert.Equal(winner.Status, actual.Status);
        Assert.Equal(winner.OutputPath, actual.OutputPath);
        Assert.Equal(winner.StartedAtUtc, actual.StartedAtUtc);
        Assert.Equal(winner.CompletedAtUtc, actual.CompletedAtUtc);
        Assert.Equal(winner.ReasonCode, actual.ReasonCode);
        Assert.Equal(winner.OutputSizeBytes, actual.OutputSizeBytes);
        Assert.Equal(winner.OutputDurationSeconds, actual.OutputDurationSeconds);
    }

    [Fact]
    public void InvalidCursorAndLimitFailClosed()
    {
        using var database = new TemporaryDatabase();
        database.Store.Initialize();
        using var history = new SqliteOrdinaryRecordingHistoryStore(database.Store);
        Assert.Throws<ArgumentOutOfRangeException>(() => history.ReadPage(101, null));
        Assert.Throws<ArgumentException>(() => history.ReadPage(20, "not a cursor"));
        Assert.Throws<ArgumentException>(() => history.Read("../arbitrary"));
    }

    private static OrdinaryRecordingHistorySnapshot Snapshot(string id, DateTimeOffset created) => new(
        id, created, "preparing", "display", "display_capture", "Display", 30, "none",
        null, null, null, null, "C:\\captures\\recording.mp4", null, null, null, null, null, null);

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
            ".local-data", "temp", "task309s", "tests", Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_root);
            Store = new SqliteOperationalStore(Path.Combine(_root, "state", "agent-recorder.db"));
        }

        public SqliteOperationalStore Store { get; }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
