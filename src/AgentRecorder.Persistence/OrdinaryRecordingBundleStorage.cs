using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class OrdinaryRecordingBundleStorage
{
    internal sealed record Fact(string MediaPath, string? OperationId, OrdinaryRecordingBundleSnapshot Snapshot);

    // Also used at the retention provider boundary. A missing fact is unknown;
    // an invalid tuple or media association must fail the whole page.
    internal static bool ValidSnapshot(OrdinaryRecordingBundleSnapshot? snapshot, string? mediaPath, string? mediaStatus)
    {
        if (snapshot is null) return true;
        if (snapshot.IdentityEvidence is not null && (snapshot.Status != "ready" || snapshot.ContentsEvidence is null)) return false;
        if (snapshot.ContentsEvidence is not null && snapshot.Status != "ready") return false;
        if (snapshot.Status == "not_applicable") return snapshot.Path is null && snapshot.ErrorCode is null;
        if (mediaStatus != "completed" || mediaPath is not { Length: > 0 and <= 4096 } ||
            !Path.IsPathFullyQualified(mediaPath) || snapshot.Path is not { Length: > 0 and <= 4096 } ||
            snapshot.Path != OrdinaryRecordingBundleOperation.DerivePath(mediaPath)) return false;
        return snapshot.Status switch
        {
            "generating" or "ready" => snapshot.ErrorCode is null,
            "failed" => OrdinaryRecordingBundleOperation.IsErrorCode(snapshot.ErrorCode),
            "interrupted_unknown" => snapshot.ErrorCode == "bundle_interrupted_unknown",
            _ => false
        };
    }

    internal static void ValidateRegistration(OrdinaryRecordingHistorySnapshot row)
    {
        if (row.BundleRegistration is not { } registration) return;
        if (row.Status is not ("completed" or "failed" or "cancelled" or "rejected" or "expired" or "interrupted_unknown"))
            throw new ArgumentException("Bundle registration requires a media terminal.");
        if (registration.Operation is { } op)
        {
            ValidateOperation(op);
            if (row.Status != "completed" || op.RecordingId != row.RecordingId || op.MediaPath != row.OutputPath)
                throw new ArgumentException("Bundle registration binding is invalid.");
        }
    }

    private static void ValidateOperation(OrdinaryRecordingBundleOperation op)
    {
        SqliteOrdinaryRecordingHistoryStore.ValidateRecordingId(op.RecordingId);
        if (op.MediaPath.Length is < 1 or > 4096 || op.BundlePath.Length is < 1 or > 4096 ||
            !Path.IsPathFullyQualified(op.MediaPath) || op.BundlePath != OrdinaryRecordingBundleOperation.DerivePath(op.MediaPath) ||
            op.OperationId.Length != 32 || op.OperationId.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Bundle operation binding is invalid.");
    }

    internal static void ValidateUpdate(OrdinaryRecordingBundleUpdate update)
    {
        OrdinaryBundleIdentityStorage.ValidateUpdate(update);
        ValidateOperation(update.Operation);
        var result = update.Result;
        if (result.Path != update.Operation.BundlePath || result.Status is not ("ready" or "failed") ||
            (result.Status == "ready" ? result.ErrorCode is not null : result.ContentsEvidence is not null || !OrdinaryRecordingBundleOperation.IsErrorCode(result.ErrorCode)))
            throw new ArgumentException("Bundle result is invalid.");
    }

    internal static void Insert(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingHistorySnapshot row)
    {
        if (row.BundleRegistration is not { } registration) return;
        ValidateRegistration(row);
        SqliteSchemaV33.Validate(connection, transaction);
        SqliteSchemaV35.Validate(connection, transaction);
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO ordinary_recording_bundles VALUES($id,$media,$op,$status,$path,NULL);";
        cmd.Parameters.AddWithValue("$id", row.RecordingId); cmd.Parameters.AddWithValue("$media", row.OutputPath);
        cmd.Parameters.AddWithValue("$op", (object?)registration.Operation?.OperationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", registration.Initial.Status); cmd.Parameters.AddWithValue("$path", (object?)registration.Initial.Path ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    internal static bool MatchesRegistration(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingHistorySnapshot row)
    {
        var fact = ReadFacts(connection, transaction, new[] { row })[row.RecordingId];
        if (row.BundleRegistration is not { } registration) return fact is null;
        if (fact is null || fact.MediaPath != row.OutputPath) return false;
        if (registration.Operation is not { } op) return fact.Snapshot.Status == "not_applicable";
        return fact.OperationId == op.OperationId && fact.Snapshot.Path == op.BundlePath;
    }

    internal static bool PersistResult(SqliteConnection connection, SqliteTransaction transaction, OrdinaryRecordingBundleUpdate update)
    {
        var op = update.Operation;
        var row = SqliteOrdinaryRecordingHistoryStore.ReadById(connection, transaction, op.RecordingId);
        if (row is null || row.Status != "completed" || row.OutputPath != op.MediaPath) return false;
        var fact = ReadFacts(connection, transaction, new[] { row })[row.RecordingId];
        if (fact is null || fact.OperationId != op.OperationId || fact.MediaPath != op.MediaPath || fact.Snapshot.Path != op.BundlePath) return false;
        if (fact.Snapshot.Status != "generating")
        {
            if (fact.Snapshot == update.Result) return true;
            if (fact.Snapshot.ContentsEvidence is not null || update.Result.ContentsEvidence is not null)
                throw new InvalidDataException("Accepted contents evidence cannot be replaced.");
            return false;
        }
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE ordinary_recording_bundles SET status_code=$status,error_code=$error
            WHERE recording_id=$id AND media_output_path=$media AND operation_id=$op
              AND bundle_path=$path AND status_code='generating';
            """;
        cmd.Parameters.AddWithValue("$status", update.Result.Status); cmd.Parameters.AddWithValue("$error", (object?)update.Result.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", op.RecordingId); cmd.Parameters.AddWithValue("$media", op.MediaPath);
        cmd.Parameters.AddWithValue("$op", op.OperationId); cmd.Parameters.AddWithValue("$path", op.BundlePath);
        if (cmd.ExecuteNonQuery() != 1) return false;
        OrdinaryBundleContentsStorage.Persist(connection, transaction, update);
        OrdinaryBundleIdentityStorage.Persist(connection, transaction, update);
        return true;
    }

    internal static void Recover(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteSchemaV31.Validate(connection, transaction);
        OrdinaryBundleContentsStorage.PrepareRecovery(connection, transaction);
        OrdinaryBundleIdentityStorage.PrepareRecovery(connection, transaction);
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        // Revoking the operation binding permanently rejects the pre-restart producer.
        cmd.CommandText = "UPDATE ordinary_recording_bundles SET status_code='interrupted_unknown',error_code='bundle_interrupted_unknown',operation_id=NULL WHERE status_code='generating';";
        cmd.ExecuteNonQuery();
    }

    internal static Dictionary<string, OrdinaryRecordingBundleSnapshot?> Read(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? observer = null,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? identityObserver = null) => ReadFacts(connection, transaction, rows, observer, identityObserver)
            .ToDictionary(p => p.Key, p => p.Value?.Snapshot, StringComparer.Ordinal);

    private static Dictionary<string, Fact?> ReadFacts(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? observer = null,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? identityObserver = null)
    {
        try
        {
            SqliteSchemaV31.Validate(connection, transaction);
            SqliteSchemaV33.Validate(connection, transaction);
            if (rows.Count > 100) throw new InvalidDataException("Bundle page exceeds its bound.");
            var facts = rows.ToDictionary(r => r.RecordingId, _ => (Fact?)null, StringComparer.Ordinal);
            if (rows.Count == 0) return facts;
            using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
            cmd.CommandText = "WITH requested(recording_id) AS (VALUES " + string.Join(",", rows.Select((_, i) => "($id" + i + ")")) +
                ") SELECT b.recording_id,b.media_output_path,b.operation_id,b.status_code,b.bundle_path,b.error_code FROM requested JOIN ordinary_recording_bundles b ON b.recording_id=requested.recording_id;";
            for (var i = 0; i < rows.Count; i++) cmd.Parameters.AddWithValue("$id" + i, rows[i].RecordingId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0); var row = rows.Single(r => r.RecordingId == id);
                var media = reader.GetString(1); var operation = reader.IsDBNull(2) ? null : reader.GetString(2);
                var status = reader.GetString(3); var path = reader.IsDBNull(4) ? null : reader.GetString(4); var error = reader.IsDBNull(5) ? null : reader.GetString(5);
                var snapshot = new OrdinaryRecordingBundleSnapshot(status, path, error);
                var operationValid = status is "not_applicable" or "interrupted_unknown" ? operation is null :
                    operation is { Length: 32 } && operation.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
                if (media != row.OutputPath || !ValidSnapshot(snapshot, media, row.Status) || !operationValid)
                    throw new InvalidDataException("Bundle history association is invalid.");
                facts[id] = new(media, operation, snapshot);
            }
            reader.Dispose();
            OrdinaryBundleContentsStorage.Read(connection, transaction, rows, facts, observer);
            OrdinaryBundleIdentityStorage.Read(connection, transaction, rows, facts, identityObserver);
            return facts;
        }
        catch (Exception e) when (e is not OrdinaryRecordingHistoryUnavailableException)
        { throw new OrdinaryRecordingHistoryUnavailableException("Bundle history is temporarily unavailable.", e); }
    }
}
