using System.Data;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>
/// Independent ordinary-video projection over the operational SQLite store.
/// Initial acceptance is synchronous; lifecycle snapshots are applied by one
/// bounded worker so SQLite latency never runs under the Engine recording lock.
/// </summary>
public sealed class SqliteOrdinaryRecordingHistoryStore : IOrdinaryRecordingHistoryStore, IOrdinaryRecordingHistoryDrainable, IOrdinaryRecordingBundleHistoryWriter
{
    private const int QueueCapacity = 128;
    private const int MaxTrackedWriteFailures = 128;
    private static readonly TimeSpan ReadFlushTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions CursorJson = new(JsonSerializerDefaults.Web);
    private readonly SqliteOperationalStore _store;
    private readonly Action<string, string>? _onWriteFailure;
    internal Action<OrdinaryRecordingHistorySnapshot>? BeforePersistForTests { get; set; }
    internal Action<OrdinaryRecordingHistorySnapshot>? AfterPersistForTests { get; set; }
    internal int UnsettledWriteFailureCountForTests { get { lock (_writeFailureGate) return _unsettledWriteFailures.Count; } }
    private readonly object _writeFailureGate = new();
    private readonly Dictionary<string, WriteFailure> _unsettledWriteFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BundleWriteFailure> _unsettledBundleFailures = new(StringComparer.Ordinal);
    private readonly object _bundleStartupRecoveryGate = new();
    private int _bundleStartupRecoveryAllowed = 1;
    // Once recovery starts, producers cannot bypass a failed startup attempt.
    // Protected by the gate; only successful Commit can complete this phase.
    private bool _bundleStartupRecoveryPending;
    internal Action<SqliteConnection, SqliteTransaction>? BeforeRecoveryCommitForTests { get; set; }
    internal Action<OrdinaryRecordingBundleUpdate>? BeforeBundlePersistForTests { get; set; }
    internal Action<OrdinaryRecordingBundleUpdate, bool>? AfterBundlePersistForTests { get; set; }
    internal Action<SqliteConnection, SqliteTransaction>? BeforeBundleCommitForTests { get; set; }
    private long _publishSequence;
    private bool _writeFailureOverflow;
    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly Task _worker;
    private int _disposed;

    public SqliteOrdinaryRecordingHistoryStore(SqliteOperationalStore store, Action<string, string>? onWriteFailure = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _onWriteFailure = onWriteFailure;
        _worker = Task.Run(WorkerLoopAsync);
    }

    public void Register(OrdinaryRecordingHistorySnapshot snapshot)
    {
        if (!TryCloseBundleStartupRecovery())
            throw new OrdinaryRecordingHistoryUnavailableException("Startup history recovery has not completed.");
        ValidateSnapshot(snapshot);
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        try
        {
            int inserted;
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO ordinary_recording_history (
                        recording_id, history_sequence, created_at_utc, status_code, source_type,
                        capture_semantics, source_title, requested_duration_seconds, audio_mode,
                        microphone_device_id, microphone_device_name, system_audio_endpoint_id,
                        system_audio_endpoint_name, output_path, nested_role, parent_recording_id,
                        nested_session_id, profile_id, profile_version, profile_digest, started_at_utc,
                        completed_at_utc, reason_code, output_size_bytes, output_duration_ms, updated_at_utc)
                    SELECT $id, (SELECT COALESCE(MAX(history_sequence), 0) + 1 FROM ordinary_recording_history), $created, $status, $source,
                        $semantics, $title, $duration, $audio, $mic_id, $mic_name, $endpoint_id,
                        $endpoint_name, $path, $nested_role, $parent, $session, $profile_id,
                        $profile_version, $profile_digest, $started, $completed, $reason, $size,
                        $output_duration, $updated
                    WHERE NOT EXISTS (SELECT 1 FROM ordinary_recording_history WHERE recording_id = $id);
                    """;
                BindSnapshot(insert, snapshot);
                insert.Parameters.AddWithValue("$created", Ticks(snapshot.CreatedAtUtc));
                insert.Parameters.AddWithValue("$updated", Ticks(snapshot.CompletedAtUtc ?? snapshot.CreatedAtUtc));
                inserted = insert.ExecuteNonQuery();
            }

            if (inserted == 1) OrdinaryRecordingTargetStorage.SetInitial(connection, transaction, snapshot);
            var persisted = ReadById(connection, transaction, snapshot.RecordingId);
            if (persisted is null || !SameAcceptedIdentity(persisted, snapshot))
                throw new InvalidOperationException("The ordinary recording history identity already exists with different accepted data.");
            var anchor = PublicationAnchorStorage.Read(connection, transaction, new[] { persisted })[persisted.RecordingId];
            if (inserted == 1)
            {
                PublicationAnchorStorage.Insert(connection, transaction, snapshot);
                OrdinaryRecordingBundleStorage.Insert(connection, transaction, snapshot);
            }
            else if (snapshot.BundleRegistration is not null && !OrdinaryRecordingBundleStorage.MatchesRegistration(connection, transaction, snapshot))
                throw new InvalidDataException("Bundle registration cannot replace a winner.");
            else if (snapshot.PublicationReceipt is { } replay && !Equals(anchor, replay.Anchor))
                throw new InvalidDataException("A publication receipt cannot replace an accepted winner.");
            transaction.Commit();
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    public bool TryPublish(OrdinaryRecordingHistorySnapshot snapshot)
    {
        if (!TryCloseBundleStartupRecovery())
        {
            NotifyStartupRecoveryBusy(snapshot?.RecordingId ?? "unknown");
            return false;
        }
        if (Volatile.Read(ref _disposed) != 0)
            return false;
        try
        {
            ValidateSnapshot(snapshot);
            var sequence = Interlocked.Increment(ref _publishSequence);
            var accepted = _queue.Writer.TryWrite(new SnapshotWork(snapshot, sequence));
            if (!accepted)
            {
                RecordWriteFailure(snapshot, sequence, "history_queue_full");
            }
            return accepted;
        }
        catch (Exception exception)
        {
            var sequence = Interlocked.Increment(ref _publishSequence);
            RecordWriteFailure(snapshot, sequence, exception.GetType().Name);
            return false;
        }
    }

    bool IOrdinaryRecordingBundleHistoryWriter.TryPublishBundle(OrdinaryRecordingBundleUpdate update) => TryPublishBundle(update);

    internal bool TryPublishBundle(OrdinaryRecordingBundleUpdate update)
    {
        try { OrdinaryRecordingBundleStorage.ValidateUpdate(update); }
        catch (ArgumentException) { return false; }
        if (!TryCloseBundleStartupRecovery())
        {
            NotifyStartupRecoveryBusy(update.Operation.RecordingId);
            return false;
        }
        var sequence = Interlocked.Increment(ref _publishSequence);
        if (Volatile.Read(ref _disposed) == 0 && _queue.Writer.TryWrite(new BundleWork(update, sequence))) return true;
        RecordBundleWriteFailure(update, sequence, Volatile.Read(ref _disposed) == 0 ? "bundle_queue_full" : "bundle_writer_closed");
        return false;
    }

    public OrdinaryRecordingHistoryPage ReadPage(int limit, string? cursor)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        FlushBeforeRead();
        CursorState? state = cursor is null ? null : DecodeCursor(cursor);
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        try
        {
            long highWater;
            if (state is null)
            {
                using var highWaterCommand = connection.CreateCommand();
                highWaterCommand.Transaction = transaction;
                highWaterCommand.CommandText = "SELECT COALESCE(MAX(history_sequence), 0) FROM ordinary_recording_history;";
                highWater = Convert.ToInt64(highWaterCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else highWater = state.HighWater;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT recording_id, history_sequence, created_at_utc, status_code, source_type,
                       capture_semantics, source_title, requested_duration_seconds, audio_mode,
                       microphone_device_id, microphone_device_name, system_audio_endpoint_id,
                       system_audio_endpoint_name, output_path, nested_role, parent_recording_id,
                       nested_session_id, profile_id, profile_version, profile_digest, started_at_utc,
                       completed_at_utc, reason_code, output_size_bytes, output_duration_ms
                FROM ordinary_recording_history
                WHERE history_sequence <= $high_water
                  AND ($before_created IS NULL OR created_at_utc < $before_created
                       OR (created_at_utc = $before_created AND recording_id COLLATE BINARY > $before_id))
                ORDER BY created_at_utc DESC, recording_id COLLATE BINARY ASC
                LIMIT $take;
                """;
            command.Parameters.AddWithValue("$high_water", highWater);
            command.Parameters.AddWithValue("$before_created", state is null ? DBNull.Value : state.BeforeCreatedTicks);
            command.Parameters.AddWithValue("$before_id", state is null ? DBNull.Value : state.BeforeRecordingId);
            command.Parameters.AddWithValue("$take", limit + 1);
            var rows = new List<(long Sequence, OrdinaryRecordingHistorySnapshot Snapshot)>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) rows.Add((reader.GetInt64(1), ReadSnapshot(reader)));
            }
            PublicationAnchorStorage.Read(connection, transaction, rows.Take(limit).Select(r => r.Snapshot).ToArray());
            var bundles = OrdinaryRecordingBundleStorage.Read(connection, transaction, rows.Take(limit).Select(r => r.Snapshot).ToArray());
            var targets = OrdinaryRecordingTargetStorage.Read(connection, transaction, rows.Take(limit).Select(r => r.Snapshot).ToArray());
            transaction.Commit();
            var hasMore = rows.Count > limit;
            var items = rows.Take(limit).Select(item => item.Snapshot with { Bundle = bundles[item.Snapshot.RecordingId], TargetKey = targets[item.Snapshot.RecordingId] }).ToArray();
            var next = hasMore && items.Length > 0
                ? EncodeCursor(new CursorState(highWater, Ticks(items[^1].CreatedAtUtc), items[^1].RecordingId))
                : null;
            return new OrdinaryRecordingHistoryPage(items, next);
        }
        catch
        {
            try { transaction.Rollback(); } catch { }
            throw;
        }
    }

    public OrdinaryRecordingHistorySnapshot? Read(string recordingId)
    {
        ValidateRecordingId(recordingId);
        FlushBeforeRead();
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
        var value = ReadById(connection, transaction, recordingId);
        PublicationAnchorStorage.Read(connection, transaction, value is null ? Array.Empty<OrdinaryRecordingHistorySnapshot>() : new[] { value });
        var bundles = OrdinaryRecordingBundleStorage.Read(connection, transaction, value is null ? Array.Empty<OrdinaryRecordingHistorySnapshot>() : new[] { value });
        transaction.Commit();
        return value is null ? null : value with { Bundle = bundles[value.RecordingId] };
    }

    internal Action? ArtifactSnapshotEstablishedForTests { get; set; }
    internal OrdinaryArtifactFacts? ReadArtifactFacts(string recordingId)
    {
        ValidateRecordingId(recordingId);
        FlushBeforeUnifiedRead();
        try
        {
            using var connection = _store.OpenReadOnlyConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
            SqliteOperationalStore.ValidateUnifiedHistorySchema(connection, transaction);
            var value = ReadById(connection, transaction, recordingId);
            ArtifactSnapshotEstablishedForTests?.Invoke();
            var rows = value is null ? Array.Empty<OrdinaryRecordingHistorySnapshot>() : new[] { value };
            var anchors = PublicationAnchorStorage.Read(connection, transaction, rows);
            var bundles = OrdinaryRecordingBundleStorage.Read(connection, transaction, rows);
            if (value is not null)
            {
                ValidateSnapshot(value);
                using var types = connection.CreateCommand(); types.Transaction = transaction;
                types.CommandText = "SELECT typeof(output_size_bytes) FROM ordinary_recording_history WHERE recording_id=$id;";
                types.Parameters.AddWithValue("$id", recordingId);
                if (types.ExecuteScalar() is not ("null" or "integer")) throw new InvalidDataException("Invalid recorded length type.");
            }
            transaction.Commit();
            return value is null ? null : new(value with { Bundle = bundles[value.RecordingId] }, anchors[value.RecordingId]);
        }
        catch (Exception ex) when (ex is not OrdinaryRecordingHistoryUnavailableException)
        { throw new OrdinaryRecordingHistoryUnavailableException("Artifact history is temporarily unavailable.", ex); }
    }

    public void RecoverIncomplete(DateTimeOffset recoveredAtUtc)
    {
        if (!Monitor.TryEnter(_bundleStartupRecoveryGate, ReadFlushTimeout))
            throw new OrdinaryRecordingHistoryUnavailableException("Startup history recovery is busy.");
        try
        {
            // A producer which won before recovery closes this phase. Otherwise
            // reserve it through Commit; SQL/Commit/flush failure keeps it pending.
            var recoverBundles = _bundleStartupRecoveryAllowed == 1;
            if (recoverBundles) _bundleStartupRecoveryPending = true;
            FlushBeforeRead();
            var now = Ticks(recoveredAtUtc);
            using var connection = _store.OpenConnection();
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE ordinary_recording_history
                SET status_code = 'interrupted_unknown', reason_code = 'process_interrupted',
                    completed_at_utc = $now, updated_at_utc = $now
                WHERE status_code NOT IN ('completed','failed','cancelled','rejected','expired','interrupted_unknown');
                """;
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
            if (recoverBundles) OrdinaryRecordingBundleStorage.Recover(connection, transaction);
            BeforeRecoveryCommitForTests?.Invoke(connection, transaction);
            transaction.Commit();
            if (recoverBundles)
            {
                _bundleStartupRecoveryPending = false;
                Volatile.Write(ref _bundleStartupRecoveryAllowed, 0);
            }
        }
        finally { Monitor.Exit(_bundleStartupRecoveryGate); }
    }

    private bool TryCloseBundleStartupRecovery()
    {
        // Normal producer callbacks never wait on recovery after this phase is
        // closed. Formal hosts complete startup recovery before accepting work.
        if (Volatile.Read(ref _bundleStartupRecoveryAllowed) == 0) return true;
        if (!Monitor.TryEnter(_bundleStartupRecoveryGate, ReadFlushTimeout)) return false;
        try
        {
            if (_bundleStartupRecoveryPending) return false;
            Volatile.Write(ref _bundleStartupRecoveryAllowed, 0);
            return true;
        }
        finally { Monitor.Exit(_bundleStartupRecoveryGate); }
    }

    private void NotifyStartupRecoveryBusy(string recordingId)
    {
        // Not admitted to the producer phase or queue: report rejection without
        // creating a queued obligation that would prevent recovery from retrying.
        try { _onWriteFailure?.Invoke(recordingId, "history_startup_recovery_busy"); } catch { }
    }

    private async Task WorkerLoopAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item is BarrierWork barrier)
            {
                barrier.Completion.TrySetResult(true);
                continue;
            }
            if (item is BundleWork bundle)
            {
                await PersistBundleWorkAsync(bundle).ConfigureAwait(false);
                continue;
            }
            if (item is not SnapshotWork update) continue;
            Exception? last = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    BeforePersistForTests?.Invoke(update.Snapshot);
                    var durablySettled = PersistUpdate(update.Snapshot);
                    last = null;
                    if (durablySettled)
                        ClearCoveredWriteFailure(update.Snapshot, update.Sequence);
                    break;
                }
                catch (Exception exception)
                {
                    last = exception;
                    if (attempt == 0) await Task.Delay(50).ConfigureAwait(false);
                }
            }
            if (last is not null)
            {
                RecordWriteFailure(update.Snapshot, update.Sequence, last.GetType().Name);
            }
            AfterPersistForTests?.Invoke(update.Snapshot);
        }
    }

    private bool PersistUpdate(OrdinaryRecordingHistorySnapshot snapshot)
    {
        using var connection = _store.OpenConnection();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        var current = ReadById(connection, transaction, snapshot.RecordingId);
        if (current is not null && current.TargetKey != snapshot.TargetKey) throw new InvalidDataException("Frozen target cannot be replaced.");
        var anchors = PublicationAnchorStorage.Read(connection, transaction, current is null ? Array.Empty<OrdinaryRecordingHistorySnapshot>() : new[] { current });
        if (current is not null && IsTerminal(current.Status))
        {
            var settled = SameTerminalResult(current, snapshot) && Equals(anchors[current.RecordingId], snapshot.PublicationReceipt?.Anchor) &&
                OrdinaryRecordingBundleStorage.MatchesRegistration(connection, transaction, snapshot);
            transaction.Commit();
            return settled;
        }
        if (current is null || !IsAllowedTransition(current.Status, snapshot.Status))
        {
            transaction.Commit();
            return false;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ordinary_recording_history
            SET status_code = $status, output_path = $path,
                started_at_utc = $started, completed_at_utc = $completed,
                reason_code = $reason, output_size_bytes = $size,
                output_duration_ms = $duration_ms, updated_at_utc = $updated
            WHERE recording_id = $id AND status_code = $current_status;
            """;
        command.Parameters.AddWithValue("$status", snapshot.Status);
        command.Parameters.AddWithValue("$path", snapshot.OutputPath);
        command.Parameters.AddWithValue("$started", DbTicks(snapshot.StartedAtUtc));
        command.Parameters.AddWithValue("$completed", DbTicks(snapshot.CompletedAtUtc));
        command.Parameters.AddWithValue("$reason", DbText(snapshot.ReasonCode));
        command.Parameters.AddWithValue("$size", DbLong(snapshot.OutputSizeBytes));
        command.Parameters.AddWithValue("$duration_ms", snapshot.OutputDurationSeconds is { } duration
            ? Math.Max(0, (long)Math.Round(duration * 1000, MidpointRounding.AwayFromZero)) : DBNull.Value);
        command.Parameters.AddWithValue("$updated", Ticks(snapshot.CompletedAtUtc ?? snapshot.StartedAtUtc ?? snapshot.CreatedAtUtc));
        command.Parameters.AddWithValue("$id", snapshot.RecordingId);
        command.Parameters.AddWithValue("$current_status", current.Status);
        var changed = command.ExecuteNonQuery();
        if (changed == 1)
        {
            PublicationAnchorStorage.Insert(connection, transaction, snapshot);
            OrdinaryRecordingBundleStorage.Insert(connection, transaction, snapshot);
        }
        transaction.Commit();
        return changed == 1;
    }

    internal void FlushBeforeRead()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (!_worker.IsCompleted) throw new OrdinaryRecordingHistoryUnavailableException("History shutdown is not drained.");
            lock (_writeFailureGate)
                if (_writeFailureOverflow || _unsettledBundleFailures.Count > 0 || HasFailedBundleRegistration())
                    throw new OrdinaryRecordingHistoryUnavailableException("Bundle history has an unsettled write failure.");
            return;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new BarrierWork(completion)) || !completion.Task.Wait(ReadFlushTimeout))
            throw new OrdinaryRecordingHistoryUnavailableException("Ordinary recording history is temporarily unavailable.");
        lock (_writeFailureGate)
        {
            if (_writeFailureOverflow || _unsettledWriteFailures.Count > 0 || _unsettledBundleFailures.Count > 0)
                throw new OrdinaryRecordingHistoryUnavailableException("Ordinary recording history has an unsettled write failure.");
        }
    }

    internal string DatabasePath => _store.DatabasePath;

    internal void FlushBeforeUnifiedRead()
    {
        FlushBeforeRead();
        // The old read method permits disposed reads. Unified reads must still
        // honor a known terminal write failure after a failed shutdown drain.
        lock (_writeFailureGate)
        {
            if (_writeFailureOverflow || _unsettledWriteFailures.Count > 0 || _unsettledBundleFailures.Count > 0)
                throw new OrdinaryRecordingHistoryUnavailableException("Ordinary recording history has an unsettled write failure.");
        }
    }

    private async Task PersistBundleWorkAsync(BundleWork work)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                BeforeBundlePersistForTests?.Invoke(work.Update);
                using var connection = _store.OpenConnection();
                using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                var settled = OrdinaryRecordingBundleStorage.PersistResult(connection, transaction, work.Update);
                BeforeBundleCommitForTests?.Invoke(connection, transaction);
                transaction.Commit();
                if (!settled)
                {
                    bool registrationFailed;
                    lock (_writeFailureGate)
                        registrationFailed = _unsettledWriteFailures.TryGetValue(work.Update.Operation.RecordingId, out var mediaFailure) &&
                            mediaFailure.Snapshot.BundleRegistration?.Operation == work.Update.Operation;
                    if (registrationFailed)
                        RecordBundleWriteFailure(work.Update, work.Sequence, "bundle_registration_unsettled");
                }
                if (settled)
                {
                    lock (_writeFailureGate)
                        if (_unsettledBundleFailures.TryGetValue(work.Update.Operation.RecordingId, out var failure) &&
                            work.Sequence > failure.Sequence && work.Update == failure.Update)
                            _unsettledBundleFailures.Remove(work.Update.Operation.RecordingId);
                }
                AfterBundlePersistForTests?.Invoke(work.Update, settled);
                return;
            }
            catch (Exception e)
            {
                last = e;
                if (attempt == 0) await Task.Delay(50).ConfigureAwait(false);
            }
        }
        RecordBundleWriteFailure(work.Update, work.Sequence, last!.GetType().Name);
    }

    // Called only under _writeFailureGate. Preserve legacy drain behavior for
    // media-only obligations, while requiring new atomic Bundle registrations.
    private bool HasFailedBundleRegistration() =>
        _unsettledWriteFailures.Values.Any(failure => failure.Snapshot.BundleRegistration is not null);

    private void RecordBundleWriteFailure(OrdinaryRecordingBundleUpdate update, long sequence, string error)
    {
        lock (_writeFailureGate)
        {
            if (_unsettledBundleFailures.TryGetValue(update.Operation.RecordingId, out var old))
            {
                if (old.Update != update) _unsettledBundleFailures[update.Operation.RecordingId] = old with { Sequence = long.MaxValue };
                else if (sequence > old.Sequence) _unsettledBundleFailures[update.Operation.RecordingId] = new(sequence, update);
            }
            else if (_unsettledBundleFailures.Count < MaxTrackedWriteFailures)
                _unsettledBundleFailures.Add(update.Operation.RecordingId, new(sequence, update));
            else _writeFailureOverflow = true;
        }
        try { _onWriteFailure?.Invoke(update.Operation.RecordingId, error); } catch { }
    }

    private void RecordWriteFailure(OrdinaryRecordingHistorySnapshot snapshot, long sequence, string errorType)
    {
        lock (_writeFailureGate)
        {
            if (_unsettledWriteFailures.TryGetValue(snapshot.RecordingId, out var existing))
            {
                if (IsTerminal(existing.Snapshot.Status))
                {
                    if (!IsTerminal(snapshot.Status))
                    {
                        // A stale active callback cannot replace a failed
                        // terminal obligation for the same recording.
                    }
                    else if (SameTerminalObligation(existing.Snapshot, snapshot) && sequence > existing.Sequence)
                    {
                        _unsettledWriteFailures[snapshot.RecordingId] = new WriteFailure(sequence, snapshot, errorType);
                    }
                    else if (!SameTerminalObligation(existing.Snapshot, snapshot))
                    {
                        // Conflicting lost terminal payloads cannot be proven
                        // repaired by either one; retain a restart-only latch.
                        _unsettledWriteFailures[snapshot.RecordingId] = existing with { Sequence = long.MaxValue };
                    }
                }
                else if (sequence > existing.Sequence)
                {
                    var required = StatusRank(snapshot.Status) >= StatusRank(existing.Snapshot.Status)
                        ? snapshot
                        : existing.Snapshot;
                    _unsettledWriteFailures[snapshot.RecordingId] = new WriteFailure(sequence, required, errorType);
                }
            }
            else if (_unsettledWriteFailures.Count < MaxTrackedWriteFailures)
            {
                _unsettledWriteFailures.Add(snapshot.RecordingId, new WriteFailure(sequence, snapshot, errorType));
            }
            else
            {
                // Bound diagnostics memory. Overflow remains unavailable until
                // restart recovery, which converts unfinished rows to unknown.
                _writeFailureOverflow = true;
            }
        }
        try { _onWriteFailure?.Invoke(snapshot.RecordingId, errorType); } catch { }
    }

    private void ClearCoveredWriteFailure(OrdinaryRecordingHistorySnapshot persisted, long sequence)
    {
        lock (_writeFailureGate)
        {
            if (!_unsettledWriteFailures.TryGetValue(persisted.RecordingId, out var failure) ||
                sequence <= failure.Sequence || !CoversFailedSnapshot(failure.Snapshot, persisted))
                return;
            _unsettledWriteFailures.Remove(persisted.RecordingId);
        }
    }

    private static bool CoversFailedSnapshot(OrdinaryRecordingHistorySnapshot failed, OrdinaryRecordingHistorySnapshot persisted)
    {
        if (failed.TargetKey != persisted.TargetKey) return false;
        if (IsTerminal(failed.Status))
            return SameTerminalObligation(failed, persisted);
        if (IsTerminal(persisted.Status))
            return true;
        return StatusRank(persisted.Status) >= StatusRank(failed.Status);
    }

    private static int StatusRank(string status) => status switch
    {
        "pending_confirmation" => 0,
        "preparing" => 1,
        "countdown" => 2,
        "recording" => 3,
        "finalizing" => 4,
        _ => -1
    };

    internal static OrdinaryRecordingHistorySnapshot? ReadById(SqliteConnection connection, SqliteTransaction? transaction, string id, bool readTarget = true)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT recording_id, history_sequence, created_at_utc, status_code, source_type,
                   capture_semantics, source_title, requested_duration_seconds, audio_mode,
                   microphone_device_id, microphone_device_name, system_audio_endpoint_id,
                   system_audio_endpoint_name, output_path, nested_role, parent_recording_id,
                   nested_session_id, profile_id, profile_version, profile_digest, started_at_utc,
                   completed_at_utc, reason_code, output_size_bytes, output_duration_ms
            FROM ordinary_recording_history WHERE recording_id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var row = reader.Read() ? ReadSnapshot(reader) : null;
        reader.Dispose();
        if (readTarget)
        {
            var targets = OrdinaryRecordingTargetStorage.Read(connection, transaction, row is null ? Array.Empty<OrdinaryRecordingHistorySnapshot>() : new[] { row });
            if (row is not null) row = row with { TargetKey = targets[row.RecordingId] };
        }
        return row;
    }

    private static OrdinaryRecordingHistorySnapshot ReadSnapshot(SqliteDataReader reader) => new(
        reader.GetString(0), FromTicks(reader.GetInt64(2)), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), NullableInt(reader, 7), reader.GetString(8),
        NullableString(reader, 9), NullableString(reader, 10), NullableString(reader, 11), NullableString(reader, 12),
        reader.GetString(13), NullableString(reader, 14), NullableString(reader, 15), NullableString(reader, 16),
        NullableString(reader, 17), NullableLong(reader, 18), NullableString(reader, 19),
        NullableTicks(reader, 20), NullableTicks(reader, 21), NullableString(reader, 22), NullableLong(reader, 23),
        NullableLong(reader, 24) is { } durationMs ? durationMs / 1000d : null);

    private static void BindSnapshot(SqliteCommand command, OrdinaryRecordingHistorySnapshot value)
    {
        command.Parameters.AddWithValue("$id", value.RecordingId);
        command.Parameters.AddWithValue("$status", value.Status);
        command.Parameters.AddWithValue("$source", value.SourceType);
        command.Parameters.AddWithValue("$semantics", value.CaptureSemantics);
        command.Parameters.AddWithValue("$title", value.SourceTitle);
        command.Parameters.AddWithValue("$duration", DbInt(value.RequestedDurationSeconds));
        command.Parameters.AddWithValue("$audio", value.AudioMode);
        command.Parameters.AddWithValue("$mic_id", DbText(value.MicrophoneDeviceId));
        command.Parameters.AddWithValue("$mic_name", DbText(value.MicrophoneDeviceName));
        command.Parameters.AddWithValue("$endpoint_id", DbText(value.SystemAudioEndpointId));
        command.Parameters.AddWithValue("$endpoint_name", DbText(value.SystemAudioEndpointName));
        command.Parameters.AddWithValue("$path", value.OutputPath);
        command.Parameters.AddWithValue("$nested_role", DbText(value.NestedRole));
        command.Parameters.AddWithValue("$parent", DbText(value.ParentRecordingId));
        command.Parameters.AddWithValue("$session", DbText(value.NestedSessionId));
        command.Parameters.AddWithValue("$profile_id", DbText(value.ProfileId));
        command.Parameters.AddWithValue("$profile_version", DbLong(value.ProfileVersion));
        command.Parameters.AddWithValue("$profile_digest", DbText(value.ProfileDigest));
        command.Parameters.AddWithValue("$started", DbTicks(value.StartedAtUtc));
        command.Parameters.AddWithValue("$completed", DbTicks(value.CompletedAtUtc));
        command.Parameters.AddWithValue("$reason", DbText(value.ReasonCode));
        command.Parameters.AddWithValue("$size", DbLong(value.OutputSizeBytes));
        command.Parameters.AddWithValue("$output_duration", value.OutputDurationSeconds is { } seconds
            ? Math.Max(0, (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero)) : DBNull.Value);
    }

    private static bool SameAcceptedIdentity(OrdinaryRecordingHistorySnapshot a, OrdinaryRecordingHistorySnapshot b) =>
        a.TargetKey == b.TargetKey && a.RecordingId == b.RecordingId && a.CreatedAtUtc == b.CreatedAtUtc && a.SourceType == b.SourceType &&
        a.CaptureSemantics == b.CaptureSemantics && a.SourceTitle == b.SourceTitle &&
        a.RequestedDurationSeconds == b.RequestedDurationSeconds && a.AudioMode == b.AudioMode &&
        a.MicrophoneDeviceId == b.MicrophoneDeviceId && a.MicrophoneDeviceName == b.MicrophoneDeviceName &&
        a.SystemAudioEndpointId == b.SystemAudioEndpointId && a.SystemAudioEndpointName == b.SystemAudioEndpointName &&
        a.OutputPath == b.OutputPath &&
        a.NestedRole == b.NestedRole && a.ParentRecordingId == b.ParentRecordingId && a.NestedSessionId == b.NestedSessionId &&
        a.ProfileId == b.ProfileId && a.ProfileVersion == b.ProfileVersion && a.ProfileDigest == b.ProfileDigest;

    private static bool IsAllowedTransition(string current, string next)
    {
        if (current == next) return true;
        if (IsTerminal(current)) return false;
        if (IsTerminal(next)) return true;
        var rank = current switch { "pending_confirmation" => 0, "preparing" => 1, "countdown" => 2, "recording" => 3, "finalizing" => 4, _ => -1 };
        var nextRank = next switch { "pending_confirmation" => 0, "preparing" => 1, "countdown" => 2, "recording" => 3, "finalizing" => 4, _ => -1 };
        return rank >= 0 && nextRank > rank;
    }

    private static bool SameTerminalResult(OrdinaryRecordingHistorySnapshot current, OrdinaryRecordingHistorySnapshot next) =>
        current.Status == next.Status && current.OutputPath == next.OutputPath &&
        current.StartedAtUtc == next.StartedAtUtc && current.CompletedAtUtc == next.CompletedAtUtc &&
        current.ReasonCode == next.ReasonCode && current.OutputSizeBytes == next.OutputSizeBytes &&
        current.OutputDurationSeconds == next.OutputDurationSeconds;

    private static bool SameTerminalObligation(OrdinaryRecordingHistorySnapshot a, OrdinaryRecordingHistorySnapshot b) =>
        a.TargetKey == b.TargetKey && SameTerminalResult(a, b) && Equals(a.PublicationReceipt, b.PublicationReceipt) && Equals(a.BundleRegistration, b.BundleRegistration);

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled" or "rejected" or "expired" or "interrupted_unknown";

    internal static void ValidateSnapshot(OrdinaryRecordingHistorySnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        OrdinaryRecordingTargetStorage.ValidateBinding(value, value.TargetKey);
        OrdinaryRecordingBundleStorage.ValidateRegistration(value);
        if (value.PublicationReceipt is { } receipt && !PublicationAnchorStorage.MatchesRow(receipt.Anchor, value))
            throw new ArgumentException("The publication receipt binding is invalid.", nameof(value));
        ValidateRecordingId(value.RecordingId);
        if (value.CreatedAtUtc.Offset != TimeSpan.Zero || !OrdinaryRecordingHistoryStatusCodes.IsKnown(value.Status) ||
            string.IsNullOrWhiteSpace(value.SourceType) || value.SourceType.Length > 64 ||
            string.IsNullOrWhiteSpace(value.CaptureSemantics) || value.CaptureSemantics.Length > 64 ||
            (value.SourceTitle?.Length ?? 0) > 512 || value.OutputPath.Length is < 1 or > 4096 ||
            value.RequestedDurationSeconds is < 1 or > 7200 ||
            value.AudioMode is not ("none" or "microphone" or "system_loopback") ||
            value.NestedRole is not (null or "outer" or "inner") ||
            (value.ProfileId is null) != (value.ProfileVersion is null) ||
            (value.ProfileId is null) != (value.ProfileDigest is null) ||
            value.ProfileVersion is <= 0 || value.OutputSizeBytes is < 0 ||
            value.OutputDurationSeconds is { } outputDuration && (!double.IsFinite(outputDuration) || outputDuration < 0) ||
            value.StartedAtUtc is { } started && (started.Offset != TimeSpan.Zero || started < value.CreatedAtUtc) ||
            value.CompletedAtUtc is { } completed && (completed.Offset != TimeSpan.Zero || completed < value.CreatedAtUtc ||
                value.StartedAtUtc is { } start && completed < start))
            throw new ArgumentException("The ordinary recording history snapshot is invalid.", nameof(value));
    }

    internal static void ValidateRecordingId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id != id.Trim() || id.Contains('/') || id.Contains('\\'))
            throw new ArgumentException("The recording ID is invalid.", nameof(id));
    }

    private static CursorState DecodeCursor(string cursor)
    {
        if (cursor.Length is < 1 or > 1024 || cursor.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("The history cursor is invalid.", nameof(cursor));
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded += new string('=', (4 - encoded.Length % 4) % 4);
            var state = JsonSerializer.Deserialize<CursorState>(Convert.FromBase64String(encoded), CursorJson);
            if (state is null || state.HighWater < 1 || state.BeforeCreatedTicks < 0 || string.IsNullOrWhiteSpace(state.BeforeRecordingId))
                throw new FormatException();
            ValidateRecordingId(state.BeforeRecordingId);
            return state;
        }
        catch { throw new ArgumentException("The history cursor is invalid.", nameof(cursor)); }
    }

    private static string EncodeCursor(CursorState state) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state, CursorJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidateSnapshotForDetails(string id) => ValidateRecordingId(id);
    private static long Ticks(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.Ticks;
    private static object DbTicks(DateTimeOffset? value) => value is { } time ? Ticks(time) : DBNull.Value;
    private static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);
    private static DateTimeOffset? NullableTicks(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : FromTicks(reader.GetInt64(index));
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static long? NullableLong(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static object DbText(string? value) => (object?)value ?? DBNull.Value;
    private static object DbInt(int? value) => value is { } number ? number : DBNull.Value;
    private static object DbLong(long? value) => value is { } number ? number : DBNull.Value;

    private sealed record CursorState(long HighWater, long BeforeCreatedTicks, string BeforeRecordingId);
    private abstract record WorkItem;
    private sealed record SnapshotWork(OrdinaryRecordingHistorySnapshot Snapshot, long Sequence) : WorkItem;
    private sealed record BundleWork(OrdinaryRecordingBundleUpdate Update, long Sequence) : WorkItem;
    private sealed record BundleWriteFailure(long Sequence, OrdinaryRecordingBundleUpdate Update);
    private sealed record BarrierWork(TaskCompletionSource<bool> Completion) : WorkItem;
    private sealed record WriteFailure(long Sequence, OrdinaryRecordingHistorySnapshot Snapshot, string ErrorType);

    public void Dispose()
    {
        DrainAndDispose(ShutdownDrainTimeout);
    }

    public bool DrainAndDispose(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        Interlocked.Exchange(ref _disposed, 1);
        _queue.Writer.TryComplete();
        try
        {
            if (!_worker.Wait(timeout) || _worker.Status != TaskStatus.RanToCompletion) return false;
            lock (_writeFailureGate) return !_writeFailureOverflow && _unsettledBundleFailures.Count == 0 && !HasFailedBundleRegistration();
        }
        catch { return false; }
    }
}
