using System.Data;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal enum RetentionGuardReason
{
    Checked, InvalidInput, Busy, HostStopped, Cancelled, TimedOut,
    CandidateRejected, BundleProtected, NestedProtected, SharedPathProtected,
    PlannedPathProtected, PendingExactProtected, PendingTemplateProtected,
    RelationUnknown, IdentityProtected, DatabaseBusy, Unavailable, ContinuationFailed
}
internal enum RetentionGuardCleanup { Closed, Unknown }
internal enum RetentionGuardContinuationResult { Completed }
internal sealed record RetentionGuardResult(RetentionGuardReason Reason);
internal sealed record RetentionGuardRetirement(RetentionGuardReason Primary, RetentionGuardCleanup Cleanup, bool Retired);

// Process-host scope. A stopped service cannot discard a late worker or create
// another slot. Tests may supply a private admission for their owned database.
internal sealed class RetentionGuardAdmission
{
    internal static readonly RetentionGuardAdmission Host = new();
    private RetentionReferenceGuard.Session? _active;
    internal bool Acquire(RetentionReferenceGuard.Session session)
        => Interlocked.CompareExchange(ref _active, session, null) is null;
    internal void Release(RetentionReferenceGuard.Session session)
        => Interlocked.CompareExchange(ref _active, null, session);
}

// Fault seams are internal, synchronous and invoked only by the owned worker.
// No provider object is available to a timer, caller or continuation.
internal class RetentionGuardDatabase
{
    internal virtual void Open(SqliteConnection connection) => connection.Open();
    internal virtual SqliteTransaction Begin(SqliteConnection connection)
        => connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
    internal virtual void Rollback(SqliteTransaction transaction) => transaction.Rollback();
    internal virtual void DisposeTransaction(SqliteTransaction transaction) => transaction.Dispose();
    internal virtual void Close(SqliteConnection connection) => connection.Close();
    internal virtual void DisposeConnection(SqliteConnection connection) => connection.Dispose();
}

// Internal protocol only; no public route, file operation or durable evidence.
internal sealed class RetentionReferenceGuard : IDisposable
{
    private readonly SqliteOperationalStore _store;
    private readonly SqliteOrdinaryRecordingHistoryStore _ordinary;
    private readonly UnifiedHistoryQueryService _history;
    private readonly string _sid, _binding;
    private readonly RetentionGuardAdmission _admission;
    private readonly RetentionGuardDatabase _database;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    internal RetentionReferenceGuard(SqliteOperationalStore store, SqliteOrdinaryRecordingHistoryStore ordinary,
        string sid, string binding, RetentionGuardAdmission? admission = null,
        RetentionGuardDatabase? database = null, TimeProvider? clock = null)
    {
        _store = store; _ordinary = ordinary; _history = new(store, ordinary);
        _sid = sid; _binding = binding; _admission = admission ?? RetentionGuardAdmission.Host;
        _database = database ?? new(); _clock = clock ?? TimeProvider.System;
    }

    internal Session Execute(string recordingId, PublicationIdentityAnchor anchor, DateTimeOffset cutoff,
        Func<ScopeContext, RetentionGuardContinuationResult> continuation, CancellationToken cancellation = default)
        => ExecuteCore(recordingId, anchor, cutoff, continuation, cancellation, null);
    internal Session ExecuteBefore(string recordingId, PublicationIdentityAnchor anchor, DateTimeOffset cutoff,
        Func<ScopeContext, RetentionGuardContinuationResult> continuation, long started, CancellationToken cancellation)
        => ExecuteCore(recordingId, anchor, cutoff, continuation, cancellation, started);
    private Session ExecuteCore(string recordingId, PublicationIdentityAnchor anchor, DateTimeOffset cutoff,
        Func<ScopeContext, RetentionGuardContinuationResult> continuation, CancellationToken cancellation, long? absoluteStarted)
    {
        var started = absoluteStarted ?? _clock.GetTimestamp(); // Includes dispatch, queue barrier and cleanup.
        var session = new Session(this, recordingId, anchor, cutoff, continuation, cancellation, started);
        if (!UnifiedHistoryQueryService.ValidIdentity(_sid) || !UnifiedHistoryQueryService.ValidIdentity(_binding) ||
            !UnifiedHistoryQueryService.ValidId(recordingId) || recordingId.IndexOfAny(new[] { '/', '\\' }) >= 0 || anchor is null || !anchor.Valid || continuation is null ||
            cutoff.Offset != TimeSpan.Zero || cutoff > _clock.GetUtcNow())
            session.Reject(RetentionGuardReason.InvalidInput);
        else if (_stop.IsCancellationRequested) session.Reject(RetentionGuardReason.HostStopped);
        else if (absoluteStarted.HasValue && _clock.GetElapsedTime(started) >= Budget) session.Reject(RetentionGuardReason.TimedOut);
        else if (!_admission.Acquire(session)) session.Reject(RetentionGuardReason.Busy);
        else session.Start();
        return session;
    }

    public void Dispose() => _stop.Cancel(); // Request only, never touches SQLite.

    internal sealed class ScopeContext
    {
        private readonly Session _session;
        private readonly int _thread;
        private int _active = 1;
        private ScopeContext(Session session) { _session = session; _thread = Environment.CurrentManagedThreadId; }
        internal string RecordingId => _session.Id;
        internal DateTimeOffset CompletedAtUtc => _session.CompletedAt!.Value;
        internal void ValidateDeletion(DeletionLedger.Prepared proof)
        { RequireActive(); _session.ValidateDeletion(proof); RequireActive(); }
        internal void RequireActive()
        {
            if (Volatile.Read(ref _active) == 0 || _thread != Environment.CurrentManagedThreadId || _session.Stopped() is not null)
                throw new InvalidOperationException("The internal reference scope has expired.");
        }
        internal static void Invoke(Session session)
        {
            session.EnterContinuation();
            var context = new ScopeContext(session);
            try
            {
                context.RequireActive();
                if (session.Continuation(context) != RetentionGuardContinuationResult.Completed)
                    throw new InvalidOperationException("Invalid synchronous continuation result.");
                context.RequireActive();
            }
            finally { Volatile.Write(ref context._active, 0); }
        }
        public override string ToString() => "RetentionReferenceScope(current-only)";
    }

    internal sealed class Session : IDisposable
    {
        internal DateTimeOffset? CompletedAt;
        internal void ValidateDeletion(DeletionLedger.Prepared proof)
        { CheckStop(); DeletionLedger.ValidatePrepared(_connection!, _transaction!, proof); CheckStop(); }
        private readonly RetentionReferenceGuard _host;
        internal string Id { get; }
        private readonly PublicationIdentityAnchor _anchor;
        private readonly DateTimeOffset _cutoff;
        internal Func<ScopeContext, RetentionGuardContinuationResult> Continuation { get; }
        private readonly CancellationToken _cancel;
        private readonly long _started;
        private readonly TaskCompletionSource<RetentionGuardResult> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<RetentionGuardRetirement> _retirement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private ITimer? _timer;
        private CancellationTokenRegistration _registration, _hostRegistration;
        private Task? _worker;
        private int _workerThread, _scopeReady, _scopeEntered;
        // Kept strongly rooted in the host admission on any uncertain cleanup.
        private SqliteConnection? _connection;
        private SqliteTransaction? _transaction;
        internal Task<RetentionGuardResult> Response => _response.Task;
        internal Task<RetentionGuardRetirement> Retirement => _retirement.Task;
        internal Session(RetentionReferenceGuard host, string id, PublicationIdentityAnchor anchor, DateTimeOffset cutoff,
            Func<ScopeContext, RetentionGuardContinuationResult> continuation, CancellationToken cancel, long started)
        { _host = host; Id = id; _anchor = anchor; _cutoff = cutoff; Continuation = continuation; _cancel = cancel; _started = started; }
        internal void Reject(RetentionGuardReason reason)
        { _response.TrySetResult(new(reason)); _retirement.TrySetResult(new(reason, RetentionGuardCleanup.Closed, true)); }
        private TimeSpan Remaining => Budget - _host._clock.GetElapsedTime(_started);
        internal RetentionGuardReason? Stopped()
            => _host._stop.IsCancellationRequested ? RetentionGuardReason.HostStopped :
                _cancel.IsCancellationRequested || Volatile.Read(ref _disposed) != 0 ? RetentionGuardReason.Cancelled :
                Remaining <= TimeSpan.Zero ? RetentionGuardReason.TimedOut : null;
        internal void Start()
        {
            try
            {
                _registration = _cancel.Register(() => Freeze(RetentionGuardReason.Cancelled));
                _hostRegistration = _host._stop.Token.Register(() => Freeze(RetentionGuardReason.HostStopped));
                _timer = _host._clock.CreateTimer(_ => Freeze(RetentionGuardReason.TimedOut), null,
                    Remaining > TimeSpan.Zero ? Remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                var worker = Task.Run(Run); _worker = worker;
                _ = worker.ContinueWith(done =>
                {
                    var result = done.Status == TaskStatus.RanToCompletion ? done.Result :
                        new RetentionGuardRetirement(RetentionGuardReason.Unavailable, RetentionGuardCleanup.Unknown, false);
                    if (done.IsFaulted) _ = done.Exception;
                    if (result.Retired) _host._admission.Release(this);
                    _retirement.TrySetResult(result);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch
            {
                if (_worker is not null)
                {
                    Freeze(RetentionGuardReason.Unavailable);
                    _retirement.TrySetResult(new(RetentionGuardReason.Unavailable, RetentionGuardCleanup.Unknown, false));
                    return; // Dispatch ownership is uncertain; never release a live worker.
                }
                _timer?.Dispose(); _registration.Dispose(); _hostRegistration.Dispose();
                _host._admission.Release(this); Reject(RetentionGuardReason.Unavailable);
            }
        }
        internal void EnterContinuation()
        {
            if (Volatile.Read(ref _scopeReady) != 1 || _workerThread != Environment.CurrentManagedThreadId ||
                Interlocked.CompareExchange(ref _scopeEntered, 1, 0) != 0)
                throw new InvalidOperationException("The internal reference scope is unavailable.");
        }
        private void Freeze(RetentionGuardReason reason) => _response.TrySetResult(new(reason));
        public void Dispose() { Interlocked.Exchange(ref _disposed, 1); Freeze(RetentionGuardReason.Cancelled); }
        private void CheckStop()
        { if (Stopped() is { } reason) throw new StopException(reason); }

        private RetentionGuardRetirement Run()
        {
            _workerThread = Environment.CurrentManagedThreadId;
            var primary = RetentionGuardReason.Unavailable;
            var cleanup = RetentionGuardCleanup.Closed;
            try
            {
                CheckStop();
                _host._ordinary.FlushBeforeUnifiedRead(Remaining); // Never inside BEGIN.
                CheckStop();
                _connection = _host._store.CreateRetentionGuardConnection();
                _host._database.Open(_connection);
                CheckStop();
                SqliteOperationalStore.ConfigureRetentionGuardConnection(_connection);
                CheckStop();
                _transaction = _host._database.Begin(_connection); // v8.0.30: BEGIN IMMEDIATE.
                CheckStop();
                SqliteOperationalStore.ValidateRetentionGuardStore(_connection, _transaction);
                var candidate = ReadExactCandidate(_host._history, _connection, _transaction, Id);
                if (candidate is null) primary = RetentionGuardReason.CandidateRejected;
                else
                {
                    var (row, entry) = candidate.Value;
                    var rows = new List<OrdinaryRecordingHistorySnapshot> { row };
                    CompletedAt = row.CompletedAtUtc;
                    if (row.RecordingId != Id || row.Status != "completed" || !entry.OutputPathRecorded || row.CompletedAtUtc is not { } at || at >= _cutoff ||
                        !PublicationAnchorStorage.MatchesRow(_anchor, row) || !string.Equals(_anchor.Path, row.OutputPath, StringComparison.Ordinal))
                        primary = RetentionGuardReason.CandidateRejected;
                    else
                    {
                        ValidateExactText(_connection, _transaction, Id, "ordinary_recording_bundles", new[] { "recording_id", "media_output_path", "operation_id", "status_code", "bundle_path", "error_code" });
                        ValidateExactText(_connection, _transaction, Id, "ordinary_publication_anchors", new[] { "recording_id", "output_path", "source" });
                        var bundle = OrdinaryRecordingBundleStorage.Read(_connection, _transaction, rows)[Id];
                        var relation = _host._history.ReadRetentionRelations(_connection, _transaction, new[] { entry }, rows).Single();
                        primary = Classify(relation, bundle, _anchor);
                        CheckStop();
                        if (primary == RetentionGuardReason.Checked)
                        {
                            Volatile.Write(ref _scopeReady, 1);
                            try { ScopeContext.Invoke(this); }
                            catch (Exception) { CheckStop(); primary = RetentionGuardReason.ContinuationFailed; }
                            finally { Volatile.Write(ref _scopeReady, 0); }
                            CheckStop();
                        }
                    }
                }
            }
            catch (StopException stop) { primary = stop.Reason; }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6) { primary = RetentionGuardReason.DatabaseBusy; }
            catch (Exception) { primary = Stopped() ?? RetentionGuardReason.Unavailable; }
            finally
            {
                // All provider operations, including disposal, stay on this worker.
                if (_transaction is not null)
                {
                    try { _host._database.Rollback(_transaction); if (_transaction.Connection is not null) cleanup = RetentionGuardCleanup.Unknown; }
                    catch { cleanup = RetentionGuardCleanup.Unknown; }
                    try { _host._database.DisposeTransaction(_transaction); }
                    catch { cleanup = RetentionGuardCleanup.Unknown; }
                }
                if (_connection is not null)
                {
                    try
                    {
                        _host._database.Close(_connection);
                        if (_connection.State != ConnectionState.Closed || _connection.Handle is not null) cleanup = RetentionGuardCleanup.Unknown;
                    }
                    catch { cleanup = RetentionGuardCleanup.Unknown; }
                    try { _host._database.DisposeConnection(_connection); }
                    catch { cleanup = RetentionGuardCleanup.Unknown; }
                }
                // Cleanup is part of the total budget. Frozen responses never change.
                try { _timer?.Dispose(); _registration.Dispose(); _hostRegistration.Dispose(); }
                catch { cleanup = RetentionGuardCleanup.Unknown; }
                Freeze(Stopped() ?? (cleanup == RetentionGuardCleanup.Closed ? primary : RetentionGuardReason.Unavailable));
                if (cleanup == RetentionGuardCleanup.Closed)
                {
                    _connection = null; _transaction = null;
                }
            }
            return new(primary, cleanup, cleanup == RetentionGuardCleanup.Closed);
        }

        private sealed class StopException(RetentionGuardReason reason) : Exception { internal RetentionGuardReason Reason => reason; }
    }

    internal static void ValidateExactText(SqliteConnection connection, SqliteTransaction transaction, string id, string table, string[] columns)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT (" + string.Join(" AND ", columns.Select(c => $"({c} IS NULL OR (typeof({c})='text' AND instr({c},char(0))=0 AND length(CAST({c} AS BLOB))<=16384))")) + ") FROM " + table + " WHERE recording_id COLLATE BINARY=$id COLLATE BINARY;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        while (reader.Read()) if (reader.GetValue(0) is not long flag || flag != 1) throw new InvalidDataException("Invalid guard association.");
    }

    private static RetentionGuardReason Classify(OrdinaryRetentionEvidence relation, OrdinaryRecordingBundleSnapshot? bundle, PublicationIdentityAnchor anchor)
    {
        if (relation.PublicationAnchor is not { } saved || saved != anchor) return RetentionGuardReason.CandidateRejected;
        if (bundle?.Status != "not_applicable") return RetentionGuardReason.BundleProtected;
        if (relation.NestedReference == OrdinaryNestedReferenceState.Invalid) return RetentionGuardReason.RelationUnknown;
        if (relation.NestedReference != OrdinaryNestedReferenceState.None) return RetentionGuardReason.NestedProtected;
        if (relation.SharedOutputReference != SharedOutputPathReferenceState.None) return RetentionGuardReason.SharedPathProtected;
        if (relation.PlannedOutputReference == PlannedOutputPathReferenceState.Indeterminate) return RetentionGuardReason.RelationUnknown;
        if (relation.PlannedOutputReference != PlannedOutputPathReferenceState.None) return RetentionGuardReason.PlannedPathProtected;
        if (relation.PendingOutputUse == PendingOutputUseState.SetupExact) return RetentionGuardReason.PendingExactProtected;
        if (relation.PendingOutputUse == PendingOutputUseState.Template) return RetentionGuardReason.PendingTemplateProtected;
        if (relation.PendingOutputUse != PendingOutputUseState.None) return RetentionGuardReason.RelationUnknown;
        return relation.PublicationIdentityReference == PublicationIdentityReferenceState.None ? RetentionGuardReason.Checked : RetentionGuardReason.IdentityProtected;
    }

    internal static (OrdinaryRecordingHistorySnapshot Row, UnifiedHistoryEntry Entry)? ReadExactCandidate(
        UnifiedHistoryQueryService history, SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        var ticks = CandidateTicks(connection, transaction, id);
        if (ticks is null) return null;
        var rows = new List<OrdinaryRecordingHistorySnapshot>();
        var entry = history.ReadOrdinary(connection, transaction, new("ordinary", id, ticks.Value, 0), false, rows);
        return (rows.Single(), entry);
    }
    private static long? CandidateTicks(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        // Validate SQL storage types and full NUL-bearing bytes before the shared
        // parser's GetString/GetInt64 can coerce or truncate corrupted values.
        var text = new[] { "recording_id", "status_code", "source_type", "capture_semantics", "source_title", "audio_mode", "output_path" };
        var optionalText = new[] { "microphone_device_id", "microphone_device_name", "system_audio_endpoint_id", "system_audio_endpoint_name", "nested_role", "parent_recording_id", "nested_session_id", "profile_id", "profile_digest", "reason_code" };
        var ints = new[] { "history_sequence", "created_at_utc" };
        var optionalInts = new[] { "requested_duration_seconds", "profile_version", "started_at_utc", "completed_at_utc", "output_size_bytes", "output_duration_ms" };
        static string Text(string c) => $"(typeof({c})='text' AND instr({c},char(0))=0 AND length(CAST({c} AS BLOB))<=16384)";
        var clauses = text.Select(Text).Concat(optionalText.Select(c => $"({c} IS NULL OR {Text(c)})"))
            .Concat(ints.Select(c => $"typeof({c})='integer'"))
            .Concat(optionalInts.Select(c => $"({c} IS NULL OR typeof({c})='integer')"));
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT created_at_utc,(" + string.Join(" AND ", clauses) + ") FROM ordinary_recording_history WHERE recording_id COLLATE BINARY=$id COLLATE BINARY LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetValue(1) is not long flag || flag != 1 || reader.GetValue(0) is not long ticks)
            throw new InvalidDataException("Invalid guard candidate.");
        return ticks;
    }
}
