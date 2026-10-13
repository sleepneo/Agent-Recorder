using System.Data;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

// Pure DB handoff. It neither flushes a writer nor calls public file projections.
internal sealed class HistoryArtifactFactsReader(SqliteOperationalStore store)
{
    internal Action? SnapshotEstablishedForTests {get;set;}
    internal Action<SqliteConnection,SqliteTransaction,string,IReadOnlyList<KeyValuePair<string,object>>>? CommandForTests {get;set;}

    internal HistoryArtifactFacts? Read(string entryId,string sid,string session)
    {
        var (source,id)=HistoryArtifactCheckService.ParseEntryId(entryId);
        if(string.IsNullOrWhiteSpace(sid)||sid.Length>256||sid!=sid.Trim()||sid.Contains('\0') ||
            string.IsNullOrWhiteSpace(session)||session.Length>256||session!=session.Trim()||session.Contains('\0'))
            throw new InvalidDataException("History identity is unavailable.");
        using var c=store.OpenReadOnlyConnection();
        using(var queryOnly=c.CreateCommand()){queryOnly.CommandText="PRAGMA query_only=ON;";queryOnly.ExecuteNonQuery();}
        using var t=c.BeginTransaction(IsolationLevel.Serializable,deferred:true);
        // Authorization against the original row precedes locator interpretation.
        // An index is never an independent owner or source of permission.
        using var owned=c.CreateCommand();owned.Transaction=t;
        owned.CommandText=source=="plan"?"""
            SELECT o.plan_id FROM plan_occurrences o WHERE o.id=$id AND (
              EXISTS(SELECT 1 FROM setup_intents WHERE plan_id=o.plan_id AND current_user_sid=$sid AND session_binding=$session) OR
              EXISTS(SELECT 1 FROM recurring_lease_local_approvals WHERE plan_id=o.plan_id AND current_user_sid=$sid AND session_binding=$session) OR
              EXISTS(SELECT 1 FROM required_once_setup_intents WHERE plan_id=o.plan_id AND current_user_sid=$sid AND session_binding=$session)) LIMIT 2;
            """:"SELECT authorization_id FROM future_window_authorizations WHERE authorization_id=$id AND current_user_sid=$sid AND session_binding=$session LIMIT 2;";
        owned.Parameters.AddWithValue("$id",id);owned.Parameters.AddWithValue("$sid",sid);owned.Parameters.AddWithValue("$session",session);
        Observe(c,t,owned);
        string original;
        using(var r=owned.ExecuteReader())
        {
            if(!r.Read()){t.Commit();return null;}
            original=r.GetValue(0) as string??throw new InvalidDataException("Invalid original history key.");
            if(r.Read())throw new InvalidDataException("Ambiguous history owner.");
        }
        SnapshotEstablishedForTests?.Invoke();
        foreach(var version in new[]{26,27,32,33,34,35,36,37,40,41})
            CheckRawRow(c,t,"schema_migrations","version=$id",version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Inspect full catalog TEXT as well: schema comparison must not accept a
        // valid prefix with a hidden NUL suffix. This is bounded schema metadata,
        // not an enumeration of history or media.
        using(var schema=c.CreateCommand())
        {
            schema.Transaction=t;
            schema.CommandText="""
                SELECT count(*) FROM sqlite_master WHERE
                  (name LIKE 'plan_media_publication_%' OR name LIKE 'future_window_publication_%' OR
                   name LIKE 'idx_plan_media_publication_%' OR name LIKE 'idx_future_window_publication_%' OR
                   name LIKE 'trg_history_%' OR name IN ('plan_history_locator','idx_plan_history_identity_created',
                    'idx_future_window_history_identity_created','idx_setup_intents_history_plan','idx_recurring_approvals_history_plan'))
                  AND (typeof(sql)!='text' OR instr(sql,char(0))!=0);
                """;
            if(schema.ExecuteScalar() is not long invalid || invalid!=0)throw new InvalidDataException("Invalid history schema text.");
        }
        SqliteOperationalStore.ValidateUnifiedHistorySchema(c,t);
        HistoryArtifactFacts result;
        if(source=="plan")
        {
            CheckRawRow(c,t,"plan_occurrences","id=$id",id);
            CheckRawRow(c,t,"plan_history_locator","occurrence_id=$id",id);
            CheckRawRow(c,t,"plans","id=$id",original);
            foreach(var table in new[]{"setup_intents","recurring_lease_local_approvals","required_once_setup_intents"})
                CheckRawRow(c,t,table,"plan_id=$id",original);
            CheckRawRow(c,t,"recurring_consent_leases","lease_id IN (SELECT lease_id FROM recurring_lease_local_approvals WHERE plan_id=$id)",original);
            CheckRawRow(c,t,"recurring_schedule_versions","plan_id=$id AND schedule_revision=(SELECT max(schedule_revision) FROM recurring_schedule_versions WHERE plan_id=$id)",original);
            CheckRawRow(c,t,"recurring_schedule_cursors","plan_id=$id AND schedule_revision=(SELECT max(schedule_revision) FROM recurring_schedule_versions WHERE plan_id=$id)",original);
            CheckRawRow(c,t,"recurring_occurrence_slots","occurrence_id=$id",id);
            foreach(var table in new[]{"recording_runs","authorized_capture_scopes",
                "recurring_occurrence_execution_specs","required_once_authorized_specs","required_once_execution_authorizations",
                "lease_uses","consent_leases"})
                CheckRawRow(c,t,table,"occurrence_id=$id",id);
            CheckRawRow(c,t,"recurring_lease_uses","run_id IN (SELECT id FROM recording_runs WHERE occurrence_id=$id)",id);
            CheckRawRow(c,t,"recording_run_output_evidence","run_id IN (SELECT id FROM recording_runs WHERE occurrence_id=$id)",id);
            using var index=c.CreateCommand();index.Transaction=t;
            index.CommandText="SELECT occurrence_sequence,plan_id,created_at_utc,owner_kind,current_user_sid,session_binding FROM plan_history_locator WHERE occurrence_id=$id LIMIT 2;";
            index.Parameters.AddWithValue("$id",id);Observe(c,t,index);
            UnifiedHistoryQueryService.Key key;
            using(var r=index.ExecuteReader())
            {
                if(!r.Read() || r.GetValue(0) is not long sequence || r.GetValue(1) is not string plan || plan!=original ||
                    r.GetValue(2) is not long ticks || r.GetValue(3) is not string kind || r.GetValue(4) is not string indexSid || indexSid!=sid ||
                    r.GetValue(5) is not string indexSession || indexSession!=session)
                    throw new InvalidDataException("History locator disagrees with its original owner.");
                key=new(source,id,ticks,sequence,plan,kind);
                if(r.Read())throw new InvalidDataException("Duplicated history locator.");
            }
            var rows=new List<PlanMediaPublicationRow>(1);
            var row=UnifiedHistoryQueryService.ReadPlan(c,t,key,sid,session,rows,probeFiles:false);
            var sizes=new Dictionary<string,long>(StringComparer.Ordinal);
            var anchors=PlanMediaPublicationStorage.Read(c,t,rows,100,CommandForTests,sizes);
            var publication=rows.SingleOrDefault();
            long? size=publication?.RequiredSize;
            if(publication is not null && sizes.TryGetValue(publication.RunId,out var savedSize))size=savedSize;
            result=new(entryId,source,row.Status!,row.OutputPath,size,
                publication is null?null:anchors[publication.RunId]);
        }
        else
        {
            CheckRawRow(c,t,"future_window_authorizations","authorization_id=$id",id);
            CheckRawRow(c,t,"future_window_publication_anchors","authorization_id=$id",id);
            var row=SqliteFutureWindowAuthorizationRepository.ReadById(c,t,id,sid,session)
                ??throw new InvalidDataException("The selected authorization disappeared.");
            FutureHistoryValidation.Validate(row);
            var anchors=FutureWindowPublicationStorage.Read(c,t,new[]{row},CommandForTests);
            result=new(entryId,source,row.StatusCode,row.StatusCode=="completed"?row.OutputPath:null,
                row.StatusCode=="completed"?row.OutputSizeBytes:null,anchors[id]);
        }
        t.Commit();return result;
    }

    private void Observe(SqliteConnection c,SqliteTransaction t,SqliteCommand cmd)=>CommandForTests?.Invoke(c,t,cmd.CommandText,
        cmd.Parameters.Cast<SqliteParameter>().Select(p=>new KeyValuePair<string,object>(p.ParameterName,p.Value!)).ToArray());

    // Validate original SQLite types and full TEXT before GetString/GetValue (which
    // can stop at NUL). Only fixed internal table/predicate names, exact indexed
    // associations, and at most two rows; never arbitrary client SQL or a scan.
    private void CheckRawRow(SqliteConnection c,SqliteTransaction t,string table,string predicate,string id)
    {
        var checks=new List<string>();
        using(var schema=c.CreateCommand())
        {
            schema.Transaction=t;schema.CommandText=$"PRAGMA table_info({table});";
            using var r=schema.ExecuteReader();
            while(r.Read())
            {
                var name=r.GetString(1);var type=r.GetString(2).ToUpperInvariant();
                if(name.Any(ch=>!char.IsAsciiLetterOrDigit(ch)&&ch!='_'))throw new InvalidDataException("Invalid history schema.");
                var column='"'+name+'"';
                var valid=type switch {
                    "TEXT"=>$"typeof({column})='text' AND instr({column},char(0))=0",
                    "INTEGER"=>$"typeof({column})='integer'",
                    "REAL"=>$"typeof({column}) IN ('integer','real')",
                    "BLOB"=>$"typeof({column})='blob'",
                    _=>throw new InvalidDataException("Invalid history schema type.")};
                checks.Add(r.GetInt64(3)==0?$"({column} IS NULL OR ({valid}))":$"({valid})");
            }
        }
        if(checks.Count==0)throw new InvalidDataException("Missing history schema.");
        using var cmd=c.CreateCommand();cmd.Transaction=t;
        cmd.CommandText=$"SELECT CASE WHEN {string.Join(" AND ",checks)} THEN 1 ELSE 0 END FROM {table} WHERE {predicate} LIMIT 2;";
        cmd.Parameters.AddWithValue("$id",id);Observe(c,t,cmd);
        using var reader=cmd.ExecuteReader();var count=0;
        while(reader.Read())
            if(++count>1 || reader.GetInt64(0)!=1)throw new InvalidDataException("Invalid original history fields.");
    }
}
