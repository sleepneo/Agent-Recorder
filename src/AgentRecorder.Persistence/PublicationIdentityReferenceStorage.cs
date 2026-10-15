using System.Buffers.Binary;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class PublicationIdentityReferenceStorage
{
    internal static IReadOnlyDictionary<string, PublicationIdentityReferenceState?> Read(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyDictionary<string, PublicationIdentityAnchor?> anchors,
        Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? inspect,
        Func<IReadOnlyList<(object CandidateId, object Flag)>, IReadOnlyList<(object CandidateId, object Flag)>>? transform)
    {
        SqliteSchemaV42.Validate(connection, transaction);
        if (anchors.Count > 100) throw new InvalidDataException("Publication reference page exceeds its bound.");
        var result = anchors.ToDictionary(a => a.Key, _ => (PublicationIdentityReferenceState?)null, StringComparer.Ordinal);
        var candidates = anchors.Where(a => a.Value is not null).ToArray();
        if (candidates.Length == 0) return result;
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "WITH requested(recording_id,volume_id,file_id) AS (VALUES " +
            string.Join(",", candidates.Select((_, i) => "($id" + i + ",$volume" + i + ",$file" + i + ")")) + ") " +
            "SELECT requested.recording_id, (" +
            "EXISTS(SELECT 1 FROM ordinary_publication_anchors a INDEXED BY " + SqliteSchemaV42.OrdinaryIndex +
            " WHERE a.volume_id=requested.volume_id AND a.file_id=requested.file_id AND a.recording_id COLLATE BINARY<>requested.recording_id COLLATE BINARY) OR " +
            "EXISTS(SELECT 1 FROM future_window_publication_anchors a INDEXED BY " + SqliteSchemaV42.FutureIndex +
            " WHERE a.volume_id=requested.volume_id AND a.file_id=requested.file_id) OR " +
            "EXISTS(SELECT 1 FROM plan_media_publication_anchors a INDEXED BY " + SqliteSchemaV42.PlanIndex +
            " WHERE a.volume_id=requested.volume_id AND a.file_id=requested.file_id)) FROM requested;";
        for (var i = 0; i < candidates.Length; i++)
        {
            var anchor = candidates[i].Value!;
            if (!anchor.Valid) throw new InvalidDataException("Invalid candidate publication identity.");
            var volume = new byte[8]; var file = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(volume, anchor.Identity.VolumeSerialNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(file, anchor.Identity.IdLow);
            BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(8), anchor.Identity.IdHigh);
            command.Parameters.AddWithValue("$id" + i, candidates[i].Key);
            command.Parameters.Add("$volume" + i, SqliteType.Blob).Value = volume;
            command.Parameters.Add("$file" + i, SqliteType.Blob).Value = file;
        }
        inspect?.Invoke(connection, transaction, command.CommandText, command.Parameters.Cast<SqliteParameter>()
            .Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        var rows = new List<(object CandidateId, object Flag)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetValue(0), reader.GetValue(1)));
        var observed = transform is null ? rows : transform(rows);
        if (observed is null || observed.Count != candidates.Length)
            throw new InvalidDataException("Missing publication reference result.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in observed)
        {
            if (row.CandidateId is not string id || !anchors.TryGetValue(id, out var anchor) || anchor is null ||
                !seen.Add(id) || row.Flag is not long flag || flag is not (0 or 1))
                throw new InvalidDataException("Invalid publication reference result.");
            result[id] = flag == 1 ? PublicationIdentityReferenceState.Referenced : PublicationIdentityReferenceState.None;
        }
        return result;
    }
}
