using System.Buffers.Binary;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

// Existing owner/lifecycle/output validation supplies these facts in the original
// query transaction. This material never enters a public projection.
internal sealed record PlanMediaPublicationRow(string PlanId, string OccurrenceId, string RunId,
    string OwnerKind, bool Completed, string? OutputPath, long? RequiredSize);

internal static class PlanMediaPublicationStorage
{
    internal static void Write(SqliteConnection connection, SqliteTransaction transaction,
        string planId, string occurrenceId, string runId, string ownerKind, string ownerId,
        string frozenPath, OutputMeta meta)
    {
        var legacy = SqliteSchemaV41.Validate(connection,transaction);
        var path = Path.GetFullPath(meta.OutputPath!); var frozen = Path.GetFullPath(frozenPath);
        var anchor = meta.PublicationReceipt?.Anchor;
        if (meta.SizeBytes <= 0 || !string.Equals(path,frozen,StringComparison.OrdinalIgnoreCase) ||
            anchor is not null && (!anchor.Valid || anchor.Length != meta.SizeBytes || !string.Equals(anchor.Path,path,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Plan publication does not match the frozen output.");
        using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT r.rowid,a.run_id,o.plan_id,o.id,r.status_code,o.status_code,
                CASE $kind WHEN 'standing' THEN s.lease_id WHEN 'recurring' THEN c.lease_id WHEN 'required_once' THEN x.execution_id END
            FROM recording_runs r JOIN plan_occurrences o ON o.id=r.occurrence_id
            LEFT JOIN plan_media_publication_anchors a ON a.run_id=r.id
            LEFT JOIN authorized_capture_scopes s ON s.plan_id=o.plan_id AND s.occurrence_id=o.id
            LEFT JOIN recurring_occurrence_execution_specs c ON c.plan_id=o.plan_id AND c.occurrence_id=o.id
            LEFT JOIN required_once_execution_authorizations x ON x.plan_id=o.plan_id AND x.occurrence_id=o.id
            WHERE r.id=$run;
            """;
        cmd.Parameters.AddWithValue("$run",runId); cmd.Parameters.AddWithValue("$kind",ownerKind);
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || reader.GetValue(0) is not long rowid || (reader.IsDBNull(1) && rowid > legacy) ||
                reader.GetValue(2) is not string plan || plan != planId || reader.GetValue(3) is not string occurrence || occurrence != occurrenceId ||
                reader.GetString(4) != "settled" || reader.GetString(5) != "completed" || reader.GetValue(6) is not string owner || owner != ownerId || reader.Read())
                throw new InvalidDataException("Plan publication winner binding is invalid.");
        }
        byte[]? volume=null,file=null;
        if (anchor is not null)
        {
            volume=new byte[8];file=new byte[16];BinaryPrimitives.WriteUInt64LittleEndian(volume,anchor.Identity.VolumeSerialNumber);
            BinaryPrimitives.WriteUInt64LittleEndian(file,anchor.Identity.IdLow);BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(8),anchor.Identity.IdHigh);
        }
        cmd.Parameters.Clear();
        cmd.CommandText = """
            INSERT INTO plan_media_publication_anchors(run_id,plan_id,occurrence_id,owner_kind,owner_id,frozen_output_path,output_path,output_size_bytes,observation_version,source,volume_id,file_id,receipt_size_bytes)
            VALUES($run,$plan,$occ,$kind,$owner,$frozen,$path,$size,$version,$source,$volume,$file,$receiptSize)
            ON CONFLICT(run_id) DO UPDATE SET plan_id=excluded.plan_id,occurrence_id=excluded.occurrence_id,owner_kind=excluded.owner_kind,
                owner_id=excluded.owner_id,frozen_output_path=excluded.frozen_output_path,output_path=excluded.output_path,output_size_bytes=excluded.output_size_bytes,
                observation_version=excluded.observation_version,source=excluded.source,volume_id=excluded.volume_id,file_id=excluded.file_id,receipt_size_bytes=excluded.receipt_size_bytes
            WHERE plan_media_publication_anchors.plan_id IS NULL AND plan_media_publication_anchors.occurrence_id IS NULL AND
                plan_media_publication_anchors.owner_kind IS NULL AND plan_media_publication_anchors.owner_id IS NULL AND
                plan_media_publication_anchors.frozen_output_path IS NULL AND plan_media_publication_anchors.output_path IS NULL AND
                plan_media_publication_anchors.output_size_bytes IS NULL AND plan_media_publication_anchors.observation_version IS NULL AND
                plan_media_publication_anchors.source IS NULL AND plan_media_publication_anchors.volume_id IS NULL AND plan_media_publication_anchors.file_id IS NULL AND plan_media_publication_anchors.receipt_size_bytes IS NULL;
            """;
        foreach(var pair in new (string,object?)[] { ("$run",runId),("$plan",planId),("$occ",occurrenceId),("$kind",ownerKind),("$owner",ownerId),
            ("$frozen",frozen),("$path",path),("$size",meta.SizeBytes),("$version",anchor is null?null:PublicationIdentityAnchor.Version),
            ("$source",anchor is null?null:PublicationIdentityAnchor.Source),("$volume",volume),("$file",file),("$receiptSize",anchor?.Length) })
            cmd.Parameters.AddWithValue(pair.Item1,pair.Item2??DBNull.Value);
        if(cmd.ExecuteNonQuery()!=1) throw new InvalidDataException("Plan publication cannot overwrite prior facts.");
    }

    internal static Dictionary<string,PublicationIdentityAnchor?> Read(SqliteConnection connection,SqliteTransaction transaction,
        IReadOnlyList<PlanMediaPublicationRow> rows,int limit,
        Action<SqliteConnection,SqliteTransaction,string,IReadOnlyList<KeyValuePair<string,object>>>? observe=null, IDictionary<string,long>? sizes=null)
    {
        var legacy=SqliteSchemaV41.Validate(connection,transaction);
        if(limit is not (100 or 512) || rows.Count>limit) throw new InvalidDataException("Plan publication page exceeds its bound.");
        var facts=rows.ToDictionary(r=>r.RunId,StringComparer.Ordinal);var result=rows.ToDictionary(r=>r.RunId,_=>(PublicationIdentityAnchor?)null,StringComparer.Ordinal);
        if(rows.Count==0)return result;
        using var cmd=connection.CreateCommand();cmd.Transaction=transaction;
        cmd.CommandText="WITH requested(run_id) AS (VALUES "+string.Join(",",rows.Select((_,i)=>"($r"+i+")"))+") "+"""
            SELECT r.id,r.rowid,o.id,o.plan_id,a.run_id,a.plan_id,a.occurrence_id,a.owner_kind,a.owner_id,a.frozen_output_path,a.output_path,a.output_size_bytes,
                a.observation_version,a.source,a.volume_id,a.file_id,s.lease_id,c.lease_id,x.execution_id,a.receipt_size_bytes,
                CASE WHEN
                  (a.plan_id IS NULL OR (typeof(a.plan_id)='text' AND instr(a.plan_id,char(0))=0)) AND
                  (a.occurrence_id IS NULL OR (typeof(a.occurrence_id)='text' AND instr(a.occurrence_id,char(0))=0)) AND
                  (a.owner_kind IS NULL OR (typeof(a.owner_kind)='text' AND instr(a.owner_kind,char(0))=0)) AND
                  (a.owner_id IS NULL OR (typeof(a.owner_id)='text' AND instr(a.owner_id,char(0))=0)) AND
                  (a.frozen_output_path IS NULL OR (typeof(a.frozen_output_path)='text' AND instr(a.frozen_output_path,char(0))=0)) AND
                  (a.output_path IS NULL OR (typeof(a.output_path)='text' AND instr(a.output_path,char(0))=0)) AND
                  (a.source IS NULL OR (typeof(a.source)='text' AND instr(a.source,char(0))=0))
                THEN 1 ELSE 0 END
            FROM requested CROSS JOIN recording_runs r ON r.id=requested.run_id
            JOIN plan_occurrences o ON o.id=r.occurrence_id
            LEFT JOIN plan_media_publication_anchors a INDEXED BY idx_plan_media_publication_run ON a.run_id=r.id
            LEFT JOIN authorized_capture_scopes s ON s.plan_id=o.plan_id AND s.occurrence_id=o.id
            LEFT JOIN recurring_occurrence_execution_specs c ON c.plan_id=o.plan_id AND c.occurrence_id=o.id
            LEFT JOIN required_once_execution_authorizations x ON x.plan_id=o.plan_id AND x.occurrence_id=o.id
            LIMIT $take;
            """;
        for(var i=0;i<rows.Count;i++)cmd.Parameters.AddWithValue("$r"+i,rows[i].RunId);cmd.Parameters.AddWithValue("$take",limit+1);
        observe?.Invoke(connection,transaction,cmd.CommandText,cmd.Parameters.Cast<SqliteParameter>().Select(p=>new KeyValuePair<string,object>(p.ParameterName,p.Value!)).ToArray());
        var seen=new HashSet<string>(StringComparer.Ordinal);using var reader=cmd.ExecuteReader();
        while(reader.Read())
        {
            // Sqlite GetString/GetValue can truncate TEXT at NUL. Validate the
            // original SQLite value before interpreting any private binding.
            if(reader.GetValue(20) is not long rawTextValid || rawTextValid!=1 ||
                reader.GetValue(0) is not string id || !facts.TryGetValue(id,out var row) || !seen.Add(id) || reader.GetValue(1) is not long sequence ||
                reader.GetValue(2) is not string occurrence || occurrence!=row.OccurrenceId || reader.GetValue(3) is not string plan || plan!=row.PlanId)
                throw new InvalidDataException("Plan publication association is invalid.");
            if(reader.IsDBNull(4)) { if(sequence>legacy)throw new InvalidDataException("Modern plan publication marker is missing.");continue; }
            if(reader.GetValue(4) is not string marker || marker!=id)throw new InvalidDataException("Plan publication key is invalid.");
            if(!row.Completed) { if(Enumerable.Range(5,11).Any(i=>!reader.IsDBNull(i)) || !reader.IsDBNull(19))throw new InvalidDataException("Orphan plan publication facts.");continue; }
            var ownerColumn=row.OwnerKind switch {"standing"=>16,"recurring"=>17,"required_once"=>18,_=>throw new InvalidDataException("Invalid plan owner kind.")};
            if(reader.GetValue(5) is not string boundPlan || boundPlan!=row.PlanId || reader.GetValue(6) is not string boundOccurrence || boundOccurrence!=row.OccurrenceId ||
                reader.GetValue(7) is not string kind || kind!=row.OwnerKind || reader.GetValue(8) is not string owner ||
                reader.GetValue(ownerColumn) is not string expectedOwner || owner!=expectedOwner ||
                reader.GetValue(9) is not string frozen || !string.Equals(frozen,row.OutputPath,StringComparison.OrdinalIgnoreCase) ||
                reader.GetValue(10) is not string path || !string.Equals(path,row.OutputPath,StringComparison.OrdinalIgnoreCase) ||
                reader.GetValue(11) is not long size || size<=0 || (row.RequiredSize is {} expectedSize && size!=expectedSize))
                throw new InvalidDataException("Plan publication output binding is invalid.");
            sizes?.Add(id,size);
            if(reader.IsDBNull(12)) { if(Enumerable.Range(13,3).Any(i=>!reader.IsDBNull(i)) || !reader.IsDBNull(19))throw new InvalidDataException("Partial plan publication identity.");continue; }
            if(reader.GetValue(19) is not long receiptSize || receiptSize!=size || reader.GetValue(12) is not long version || version!=PublicationIdentityAnchor.Version || reader.GetValue(13) is not string source || source!=PublicationIdentityAnchor.Source ||
                reader.GetValue(14) is not byte[] volume || volume.Length!=8 || reader.GetValue(15) is not byte[] file || file.Length!=16)
                throw new InvalidDataException("Plan publication identity format is invalid.");
            var anchor=new PublicationIdentityAnchor(path,size,new(BinaryPrimitives.ReadUInt64LittleEndian(volume),BinaryPrimitives.ReadUInt64LittleEndian(file),BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(8))));
            if(!anchor.Valid)throw new InvalidDataException("Invalid plan publication identity.");result[id]=anchor;
        }
        if(seen.Count!=rows.Count)throw new InvalidDataException("Plan publication row disappeared.");return result;
    }
}
