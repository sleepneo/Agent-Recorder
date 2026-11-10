using System.Diagnostics;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record ManagedVideoUsageSample(
    long? RecordedLogicalBytes, int ScopedRecordings, int IncludedRecordings, int IncludedObjects,
    int UnknownRecordings, int ExcludedSettledRecordings, int ExcludedSettledDispositions,
    DateTimeOffset SampledAtUtc, string[] ReasonCodes, string? UnavailableReason = null);

internal sealed class ManagedVideoUsage(SqliteOperationalStore store, string trustedDataDirectory)
{
    internal const string Scope = "managed_ordinary_main_mp4";
    internal const int HistoryLimit = 4096;
    internal const int LedgerLimit = 4096;
    internal const int ReceiptValidationLimit = 512;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private readonly string _videosRoot = Path.Combine(Path.GetFullPath(trustedDataDirectory), "Videos");

    internal Action? BeforeReadForTests { get; init; }
    internal Func<CancellationToken, Task>? DeadlineForTests { get; init; }
    internal Action? WorkerRetiredForTests { get; init; }

    private sealed class UsageObject(PublicationIdentityAnchor anchor)
    {
        internal PublicationIdentityAnchor Anchor { get; } = anchor;
        internal Dictionary<string, DateTimeOffset> Records { get; } = new(StringComparer.Ordinal);
        internal DateTimeOffset LatestCompletion { get; set; }
        internal bool Conflict { get; set; }
        internal bool Settled { get; set; }
    }

    internal async Task<ManagedVideoUsageSample> ReadAsync(CancellationToken cancellation = default)
    {
        var started = Stopwatch.GetTimestamp();
        var sample = await ArtifactMetadataWorker.ReadAsync(
            token => Read(started, token), Unavailable, DeadlineForTests ?? (token => Task.Delay(Timeout, token)),
            WorkerRetiredForTests, cancellation).ConfigureAwait(false);
        return sample ?? Unavailable("failed");
    }

    private static ManagedVideoUsageSample Unavailable(string reason) =>
        new(null, 0, 0, 0, 0, 0, 0, default, [], reason);

    private ManagedVideoUsageSample Read(long started, CancellationToken token)
    {
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= Timeout) throw new TimeoutException();
        }

        try
        {
            Check();
            BeforeReadForTests?.Invoke();
            Check();
            if (!OriginalFileHoldService.ValidScopePath(_videosRoot, Path.Combine(_videosRoot, "scope.mp4")))
                return Unavailable("scope_unavailable");
            ManagedVideoUsageSample sample;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
                Pooling = false, DefaultTimeout = 1
            }.ToString()))
            {
                connection.Open();
                Check();
                SqliteOperationalStore.ConfigureRetentionGuardConnection(connection);
                using var transaction = connection.BeginTransaction(deferred: true);
                var at = DateTimeOffset.UtcNow;
                SqliteOperationalStore.ValidateRetentionGuardStore(connection, transaction);
                SqliteSchemaV30.Validate(connection, transaction);
                Check();
                sample = Aggregate(connection, transaction, at, Check);
            }
            Check();
            return sample;
        }
        catch (OperationCanceledException) { return Unavailable("cancelled"); }
        catch (TimeoutException) { return Unavailable("timeout"); }
        catch (UsageLimitException ex) { return Unavailable(ex.Reason); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        { return Unavailable("data_invalid"); }
        catch (SqliteOperationalStoreException ex) when (ex.Code == "sqlite_corrupt") { return Unavailable("data_invalid"); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) { return Unavailable("data_invalid"); }
        catch (Exception) { return Unavailable("failed"); }
    }

    private ManagedVideoUsageSample Aggregate(SqliteConnection connection, SqliteTransaction transaction,
        DateTimeOffset at, Action check)
    {
        var objects = ReadHistory(connection, transaction, check, out var missing);
        var dispositions = ReadSettlements(connection, transaction, objects, check);
        var conflicts = 0;
        var included = 0;
        var includedObjects = 0;
        var excluded = 0;
        long bytes = 0;
        foreach (var item in objects.Values)
        {
            check();
            if (item.Conflict) conflicts += item.Records.Count;
            else if (item.Settled) excluded += item.Records.Count;
            else
            {
                included += item.Records.Count;
                includedObjects++;
                bytes = checked(bytes + item.Anchor.Length);
            }
        }

        var reasons = new List<string>(2);
        if (missing != 0) reasons.Add("publication_evidence_missing");
        if (conflicts != 0) reasons.Add("publication_identity_conflict");
        var unknown = missing + conflicts;
        return new(included == 0 && unknown != 0 ? null : bytes, included + excluded + unknown,
            included, includedObjects, unknown, excluded, dispositions, at, reasons.ToArray());
    }

    private Dictionary<ObservedFileIdentity, UsageObject> ReadHistory(SqliteConnection connection,
        SqliteTransaction transaction, Action check, out int missing)
    {
        var objects = new Dictionary<ObservedFileIdentity, UsageObject>();
        int unanchored = 0;
        ReadRegistrations(connection, transaction, _videosRoot, check, row =>
        {
            if (row.Anchor is not { } anchor)
            {
                unanchored++;
                return;
            }
            if (!objects.TryGetValue(anchor.Identity, out var item))
                objects.Add(anchor.Identity, item = new UsageObject(anchor));
            if (!Paths.Equals(item.Anchor.Path, anchor.Path) || item.Anchor.Length != anchor.Length)
                item.Conflict = true;
            var completed = row.Completed!.Value;
            item.Records.Add(row.Id, completed);
            if (completed > item.LatestCompletion) item.LatestCompletion = completed;
        });
        missing = unanchored;
        return objects;
    }

    internal sealed record Registration(string Id, string Path, DateTimeOffset? Completed,
        PublicationIdentityAnchor? Anchor, bool ThroughRootAlias = false);

    // Same bounded published-history projection for usage and reconciliation.
    // Reconciliation needs registration facts, not the deletion-ledger-adjusted subtotal.
    internal static void ReadRegistrations(SqliteConnection connection, SqliteTransaction transaction,
        string videosRoot, Action check, Action<Registration> visit, Func<string, bool>? matchesRootAlias = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN typeof(h.recording_id)='text' AND length(CAST(h.recording_id AS BLOB))<=512
                        AND instr(h.recording_id,char(0))=0 THEN h.recording_id END,
                   CASE WHEN typeof(h.output_path)='text' AND length(CAST(h.output_path AS BLOB)) BETWEEN 1 AND 16384
                        AND instr(h.output_path,char(0))=0 THEN h.output_path END,
                   CASE WHEN typeof(h.created_at_utc)='integer' THEN h.created_at_utc END,
                   CASE WHEN typeof(h.completed_at_utc) IN ('integer','null') THEN h.completed_at_utc ELSE 'invalid' END,
                   CASE WHEN typeof(h.output_size_bytes) IN ('integer','null') THEN h.output_size_bytes ELSE 'invalid' END,
                   a.recording_id IS NOT NULL,
                   CASE WHEN typeof(a.output_path)='text' AND length(CAST(a.output_path AS BLOB))<=4096
                        AND instr(a.output_path,char(0))=0 THEN a.output_path END,
                   CASE WHEN typeof(a.output_size_bytes)='integer' THEN a.output_size_bytes END,
                   CASE WHEN typeof(a.observation_version)='integer' THEN a.observation_version END,
                   CASE WHEN typeof(a.source)='text' AND length(CAST(a.source AS BLOB))<=64
                        AND instr(a.source,char(0))=0 THEN a.source END,
                   CASE WHEN typeof(a.volume_id)='blob' AND length(a.volume_id)=8 THEN a.volume_id END,
                   CASE WHEN typeof(a.file_id)='blob' AND length(a.file_id)=16 THEN a.file_id END
            FROM ordinary_recording_history AS h INDEXED BY idx_ordinary_recording_history_status_created
            LEFT JOIN ordinary_publication_anchors AS a ON a.recording_id=h.recording_id
            WHERE h.status_code='completed' LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", HistoryLimit + 1);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            check();
            if (++count > HistoryLimit) throw new UsageLimitException("history_limit");
            if (reader.GetValue(1) is not string path) throw new InvalidDataException();
            var throughAlias = !OriginalFileHoldService.ValidScopePath(videosRoot, path);
            if (throughAlias && matchesRootAlias?.Invoke(path) != true) continue;

            if (reader.GetValue(0) is not string id || !UnifiedHistoryQueryService.ValidId(id) ||
                reader.GetValue(2) is not long created || created < 0 || created > DateTimeOffset.MaxValue.UtcTicks)
                throw new InvalidDataException();
            var completedTicks = OptionalInteger(reader, 3);
            if (completedTicks is { } ticks && (ticks < created || ticks > DateTimeOffset.MaxValue.UtcTicks))
                throw new InvalidDataException();
            var size = OptionalInteger(reader, 4);
            if (size < 0) throw new InvalidDataException();
            if (reader.GetInt64(5) == 0)
            {
                visit(new(id, path, null, null, throughAlias));
                continue;
            }

            var anchor = PublicationAnchorStorage.ReadAnchor(reader, 6);
            DateTimeOffset? completed = completedTicks is { } value ? new(value, TimeSpan.Zero) : null;
            if (!PublicationAnchorStorage.MatchesCompletedRow(anchor, path, size, completed))
                throw new InvalidDataException();
            visit(new(id, path, completed, anchor, throughAlias));
        }
    }

    private int ReadSettlements(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<ObservedFileIdentity, UsageObject> objects, Action check)
    {
        var candidates = FindSettlementCandidates(connection, transaction, objects, check);
        var dispositions = 0;
        foreach (var operation in candidates)
        {
            check();
            var entry = DeletionLedger.ReadRow(connection, transaction, operation) ?? throw new InvalidDataException();
            if (entry is not { State: DeletionRecoveryState.Settled, Progress: LocalDeletionProgress.DispositionAccepted,
                    Reason: LocalDeletionReason.None, Win32Error: null, OurResourcesRetired: true })
                continue;
            var binding = entry.Binding;
            if (!MatchesObject(objects, binding.Root, binding.Anchor.Path, binding.Anchor.Length, binding.Anchor.Identity, out var item))
                throw new InvalidDataException();
            // An earlier operation cannot settle a later registration of a reused identity.
            if (!item.Records.TryGetValue(binding.RecordingId, out var completed) || completed != binding.Completed ||
                item.LatestCompletion >= binding.PreparedAt)
                continue;
            item.Settled = true;
            dispositions++;
        }
        return dispositions;
    }

    private List<string> FindSettlementCandidates(SqliteConnection connection, SqliteTransaction transaction,
        Dictionary<ObservedFileIdentity, UsageObject> objects, Action check)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN typeof(operation_id)='text' AND length(CAST(operation_id AS BLOB))=32
                        AND instr(operation_id,char(0))=0 THEN operation_id END,
                   CASE WHEN typeof(phase)='text' AND length(CAST(phase AS BLOB))<=16
                        AND instr(phase,char(0))=0 THEN phase END,progress,
                   CASE WHEN typeof(videos_root)='text' AND length(CAST(videos_root AS BLOB))<=4096
                        AND instr(videos_root,char(0))=0 THEN videos_root END,
                   CASE WHEN typeof(output_path)='text' AND length(CAST(output_path AS BLOB))<=4096
                        AND instr(output_path,char(0))=0 THEN output_path END,recorded_bytes,
                   CASE WHEN typeof(volume_id)='blob' AND length(volume_id)=8 THEN volume_id END,
                   CASE WHEN typeof(file_id)='blob' AND length(file_id)=16 THEN file_id END
            FROM ordinary_deletion_operations ORDER BY operation_id COLLATE BINARY LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", LedgerLimit + 1);
        using var reader = command.ExecuteReader();
        var candidates = new List<string>();
        var count = 0;
        while (reader.Read())
        {
            check();
            if (++count > LedgerLimit) throw new UsageLimitException("ledger_limit");
            if (reader.GetValue(0) is not string operation || !DeletionLedger.Id(operation))
                throw new InvalidDataException();
            var progress = OptionalInteger(reader, 2);
            if (reader.GetValue(1) is not string phase ||
                !(phase == "prepared_unknown" && progress is null || phase == "settled" && progress is 0 or 2 or 3 or 4))
                throw new InvalidDataException();
            if (progress != (int)LocalDeletionProgress.DispositionAccepted) continue;
            if (reader.GetValue(3) is not string root || reader.GetValue(4) is not string path ||
                reader.GetValue(5) is not long bytes || bytes <= 0)
                throw new InvalidDataException();
            var identity = PublicationAnchorStorage.ReadIdentity(reader, 6);
            if (!MatchesObject(objects, root, path, bytes, identity, out _)) continue;
            if (candidates.Count == ReceiptValidationLimit) throw new UsageLimitException("receipt_validation_limit");
            candidates.Add(operation);
        }
        return candidates;
    }

    private bool MatchesObject(Dictionary<ObservedFileIdentity, UsageObject> objects, string root, string path,
        long bytes, ObservedFileIdentity identity, out UsageObject item)
    {
        if (objects.TryGetValue(identity, out var found) && !found.Conflict && Paths.Equals(root, _videosRoot) &&
            Paths.Equals(found.Anchor.Path, path) && found.Anchor.Length == bytes)
        {
            item = found;
            return true;
        }
        item = null!;
        return false;
    }

    private static long? OptionalInteger(SqliteDataReader reader, int index) =>
        reader.GetValue(index) switch { DBNull => null, long number => number, _ => throw new InvalidDataException() };

    internal sealed class UsageLimitException(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }
}
