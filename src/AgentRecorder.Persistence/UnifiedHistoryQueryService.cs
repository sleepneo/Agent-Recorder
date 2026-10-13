using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

/// <summary>One read-only snapshot over indexed, identity-bound candidates.</summary>
public sealed class UnifiedHistoryQueryService
{
    private readonly SqliteOperationalStore _store;
    private readonly SqliteOrdinaryRecordingHistoryStore _ordinary;
    private readonly bool _ordinaryOnly;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int CursorVersion = 2301; // optional null filters preserve older query cursors
    private const long UnixEpochTicks = 621355968000000000;
    internal Action<string, int>? CandidatesSelectedForTests { get; set; }
    internal Action<string, int>? EntriesValidatedForTests { get; set; }
    internal Action? SnapshotEstablishedForTests { get; set; }
    internal Action<string, string, IReadOnlyList<KeyValuePair<string, object>>>? CandidateCommandForTests { get; set; }
    internal Func<string, bool>? OrdinaryFileProbeForTests { get; set; }
    internal Action<string, IReadOnlyList<KeyValuePair<string, object>>>? TargetCommandForTests { get; set; }
    internal Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? BundleContentsCommandForTests { get; set; }
    internal Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? BundleIdentityCommandForTests { get; set; }
    internal Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? AttachmentHashCommandForTests { get; set; }
    internal Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? MediaHashCommandForTests { get; set; }

    public UnifiedHistoryQueryService(SqliteOperationalStore store, SqliteOrdinaryRecordingHistoryStore ordinary, bool ordinaryOnly = false)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(ordinary);
        if (!string.Equals(store.DatabasePath, ordinary.DatabasePath, StringComparison.Ordinal))
            throw new ArgumentException("The unified reader and ordinary writer must share the operational store.");
        _store = store; _ordinary = ordinary; _ordinaryOnly = ordinaryOnly;
    }

    public IReadOnlyList<string> SupportedSources => _ordinaryOnly
        ? new[] { "ordinary" } : new[] { "ordinary", "plan", "future_window" };

    public UnifiedHistoryPage GetPage(string sid, string session, int limit, UnifiedHistoryFilters filters, string? cursor = null)
        => GetPageCore(sid, session, limit, filters, cursor, probeOrdinaryFiles: true);

    // Internal retention read: identical indexed selection, barrier, validation,
    // identity/cursor and transaction, but no file probe before root validation.
    internal OrdinaryRetentionEvidencePage GetOrdinaryEvidencePage(string sid,string session,int limit,
        DateTimeOffset completedBefore,string? cursor)
    {
        IReadOnlyList<OrdinaryRetentionEvidence>? evidence=null;
        var page=GetPageCore(sid,session,limit,new("ordinary",CreatedBefore:completedBefore),cursor,false,
            (connection,transaction,entries,rows)=>evidence=ReadRetentionRelations(connection,transaction,entries,rows));
        return new(evidence??throw new InvalidDataException("Retention relation evidence is missing."),page.NextCursor,page.PageSize,page.Filters);
    }
    internal Action<string,IReadOnlyList<KeyValuePair<string,object>>>? RetentionReferenceCommandForTests {get;set;}
    private IReadOnlyList<OrdinaryRetentionEvidence> ReadRetentionRelations(SqliteConnection connection,SqliteTransaction transaction,
        IReadOnlyList<UnifiedHistoryEntry> entries,IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows)
    {
        SqliteOperationalStore.ValidateRetentionReferenceSchema(connection,transaction);
        SqliteOperationalStore.ValidateRetentionOutputPathSchema(connection,transaction);
        SqliteOperationalStore.ValidateRetentionPlannedOutputSchema(connection,transaction);
        SqliteOperationalStore.ValidateRetentionPendingOutputSchema(connection,transaction);
        SqliteOperationalStore.ValidateRetentionRequiredOnceSetupOutputSchema(connection,transaction);
        if(entries.Count!=rows.Count || rows.Count>100) throw new InvalidDataException("Invalid retention relation page.");
        var anchors = PublicationAnchorStorage.Read(connection, transaction, rows);
        if(rows.Count==0) return Array.Empty<OrdinaryRetentionEvidence>();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        var parameters=rows.Select((row,i)=>"($parent"+i+")").ToArray();
        command.CommandText="WITH requested(recording_id) AS (VALUES "+string.Join(",",parameters)+") "+
            "SELECT requested.recording_id, EXISTS(SELECT 1 FROM ordinary_recording_history child INDEXED BY "+SqliteSchemaV25.IndexName+
            " WHERE child.parent_recording_id=requested.recording_id) FROM requested;";
        for(var i=0;i<rows.Count;i++) Add(command,"$parent"+i,rows[i].RecordingId);
        RetentionReferenceCommandForTests?.Invoke(command.CommandText,command.Parameters.Cast<SqliteParameter>()
            .Select(p=>new KeyValuePair<string,object>(p.ParameterName,p.Value!)).ToArray());
        var references=new Dictionary<string,bool>(StringComparer.Ordinal);
        using(var reader=command.ExecuteReader())
            while(reader.Read())
            {
                var flag=reader.GetInt64(1);
                if(flag is not (0 or 1) || !references.TryAdd(reader.GetString(0),flag==1)) throw new InvalidDataException("Invalid retention relation result.");
            }
        if(references.Count!=rows.Count) throw new InvalidDataException("Missing retention relation result.");
        var shared = ReadSharedOutputReferences(connection, transaction, rows);
        var planned = ReadPlannedOutputReferences(connection, transaction, rows);
        var pending = PendingOutputUseStorage.Read(connection, transaction, rows, RetentionPendingOutputCommandForTests);
        return entries.Select((entry,i)=>new OrdinaryRetentionEvidence(entry,NestedState(rows[i],references[rows[i].RecordingId]),
            shared[rows[i].RecordingId] ? SharedOutputPathReferenceState.Referenced : SharedOutputPathReferenceState.None, planned[rows[i].RecordingId]) { PublicationAnchor = anchors[rows[i].RecordingId], PendingOutputUse = pending[rows[i].RecordingId] }).ToArray();
    }
    internal Action<SqliteConnection,SqliteTransaction,string,IReadOnlyList<KeyValuePair<string,object>>>? RetentionPendingOutputCommandForTests { get; set; }
    internal Action<SqliteConnection,SqliteTransaction,string,IReadOnlyList<KeyValuePair<string,object>>>? RetentionPathCommandForTests { get; set; }
    private Dictionary<string,bool> ReadSharedOutputReferences(SqliteConnection connection,SqliteTransaction transaction,
        IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var requested = rows.Select((row,i)=>"($id"+i+",$path"+i+")");
        var exists = SqliteSchemaV28.Paths.Select(p => "EXISTS(SELECT 1 FROM "+p.Table+" referenced INDEXED BY "+p.Index+
            " WHERE referenced.output_path COLLATE "+SqliteOutputPathCollation.Name+"=requested.output_path"+
            (p.Table=="ordinary_recording_history"?" AND referenced.recording_id<>requested.recording_id":"")+")");
        command.CommandText = "WITH requested(recording_id,output_path) AS (VALUES "+string.Join(",",requested)+") "+
            "SELECT requested.recording_id, ("+string.Join(" OR ",exists)+") FROM requested;";
        for (var i=0;i<rows.Count;i++) { Add(command,"$id"+i,rows[i].RecordingId);Add(command,"$path"+i,rows[i].OutputPath); }
        RetentionPathCommandForTests?.Invoke(connection,transaction,command.CommandText,command.Parameters.Cast<SqliteParameter>()
            .Select(p=>new KeyValuePair<string,object>(p.ParameterName,p.Value!)).ToArray());
        var result = new Dictionary<string,bool>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var value = reader.GetInt64(1);
            if (value is not (0 or 1) || !result.TryAdd(reader.GetString(0),value==1)) throw new InvalidDataException("Invalid path reference result.");
        }
        if (result.Count != rows.Count) throw new InvalidDataException("Missing path reference result.");
        return result;
    }
    internal Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? RetentionPlannedPathCommandForTests { get; set; }
    // Only slice candidate raw text. Neither normalize foreign rows nor resolve
    // any path. Keep the drive-root separator for both bounded directory forms.
    internal static (string? WithoutTail, string? WithTail, string? Name) SplitFrozenTarget(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('/') || path.Contains('\0'))
            return (null, null, null);
        var split = path.LastIndexOf('\\');
        if (split < 2 || split == path.Length - 1) return (null, null, null);
        return (split == 2 ? path[..3] : path[..split], path[..(split + 1)], path[(split + 1)..]);
    }
    private Dictionary<string, PlannedOutputPathReferenceState> ReadPlannedOutputReferences(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var targets = rows.Select(row => SplitFrozenTarget(row.OutputPath)).ToArray();
        var requested = rows.Select((row,i) => "($id"+i+",$path"+i+",$dir"+i+",$tail"+i+",$name"+i+")");
        var exists = SqliteSchemaV29.Paths.Select(p => "EXISTS(SELECT 1 FROM "+p.Table+" frozen INDEXED BY "+p.Index+
            (p.Columns.Length == 1 ? " WHERE frozen.frozen_output_file_path COLLATE "+SqliteOutputPathCollation.Name+"=requested.output_path" :
            " WHERE frozen.output_directory COLLATE "+SqliteOutputPathCollation.Name+" IN (requested.directory,requested.directory_tail) AND frozen."+p.Columns[1]+
            " COLLATE "+SqliteOutputPathCollation.Name+"=requested.file_name")+")");
        command.CommandText = "WITH requested(recording_id,output_path,directory,directory_tail,file_name) AS (VALUES "+string.Join(",",requested)+") "+
            "SELECT requested.recording_id, ("+string.Join(" OR ",exists)+") FROM requested;";
        for (var i=0; i<rows.Count; i++)
        { Add(command,"$id"+i,rows[i].RecordingId); Add(command,"$path"+i,rows[i].OutputPath); Add(command,"$dir"+i,(object?)targets[i].WithoutTail??DBNull.Value);
          Add(command,"$tail"+i,(object?)targets[i].WithTail??DBNull.Value); Add(command,"$name"+i,(object?)targets[i].Name??DBNull.Value); }
        RetentionPlannedPathCommandForTests?.Invoke(connection,transaction,command.CommandText,command.Parameters.Cast<SqliteParameter>()
            .Select(p=>new KeyValuePair<string,object>(p.ParameterName,p.Value!)).ToArray());
        var flags = new Dictionary<string, bool>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var flag = reader.GetInt64(1);
            if (flag is not (0 or 1) || !flags.TryAdd(reader.GetString(0),flag==1)) throw new InvalidDataException("Invalid planned path reference result.");
        }
        if (flags.Count != rows.Count) throw new InvalidDataException("Missing planned path reference result.");
        return rows.Select((row,i)=>(row.RecordingId, State: flags[row.RecordingId] ? PlannedOutputPathReferenceState.Referenced :
            targets[i].Name is null ? PlannedOutputPathReferenceState.Indeterminate : PlannedOutputPathReferenceState.None))
            .ToDictionary(p=>p.RecordingId,p=>p.State,StringComparer.Ordinal);
    }

    private static OrdinaryNestedReferenceState NestedState(OrdinaryRecordingHistorySnapshot row,bool referenced)
    {
        try
        {
            // Other row evidence was already validated. Reuse the existing role
            // and ID rules here, without relaxing public history or writer validation.
            SqliteOrdinaryRecordingHistoryStore.ValidateSnapshot(row);
            if(row.ParentRecordingId is { } parent)
            { SqliteOrdinaryRecordingHistoryStore.ValidateRecordingId(parent);if(!ValidId(parent)) throw new InvalidDataException(); }
            if(row.NestedSessionId is { } session && !ValidId(session)) throw new InvalidDataException();
        }
        catch(Exception e) when(e is ArgumentException or InvalidDataException) {return OrdinaryNestedReferenceState.Invalid;}
        return row.NestedRole is not null || row.ParentRecordingId is not null || row.NestedSessionId is not null?
            OrdinaryNestedReferenceState.Relationship:referenced?OrdinaryNestedReferenceState.ParentReferenced:OrdinaryNestedReferenceState.None;
    }

    private UnifiedHistoryPage GetPageCore(string sid, string session, int limit, UnifiedHistoryFilters filters,
        string? cursor, bool probeOrdinaryFiles,
        Action<SqliteConnection,SqliteTransaction,IReadOnlyList<UnifiedHistoryEntry>,IReadOnlyList<OrdinaryRecordingHistorySnapshot>>? retention=null)
    {
        ValidateArguments(limit, filters);
        if (_ordinaryOnly && filters.Source is "plan" or "future_window") throw new UnifiedHistoryArgumentException();
        if (!ValidIdentity(sid) || !ValidIdentity(session))
            throw new UnifiedHistoryUnavailableException(new InvalidDataException("Server identity is unavailable."));
        // Cursor parsing errors are deliberately outside the persistence catch.
        var state = cursor is null ? null : Decode(cursor);
        var identity = Digest(_store.DatabasePath + "\0" + sid + "\0" + session + "\0" + (_ordinaryOnly ? "ordinary" : "all"));
        if (state is not null && (state.Version != CursorVersion || state.Identity != identity || state.Filters != filters ||
            state.OrdinaryWatermark < 0 || state.PlanWatermark < 0 || state.FutureWatermark < 0 ||
            state.LastTicks < 0 || state.LastTicks > DateTime.MaxValue.Ticks ||
            state.LastSource is not ("ordinary" or "plan" or "future_window") || !ValidId(state.LastId)))
            throw new UnifiedHistoryArgumentException();
        try
        {
            // No writer locks or SQLite read transaction are held while waiting.
            _ordinary.FlushBeforeUnifiedRead();
            using var connection = _store.OpenReadOnlyConnection();
            using (var readOnly = connection.CreateCommand())
            { readOnly.CommandText = "PRAGMA query_only=ON;"; readOnly.ExecuteNonQuery(); }
            using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: true);
            SqliteOperationalStore.ValidateUnifiedHistorySchema(connection, transaction);
            state ??= new(CursorVersion, identity, filters,
                Max(connection, transaction, "ordinary_recording_history", "history_sequence"),
                _ordinaryOnly ? 0 : Max(connection, transaction, "plan_occurrences", "rowid"),
                _ordinaryOnly ? 0 : Max(connection, transaction, "future_window_authorizations", "rowid"),
                null, null, null);
            SnapshotEstablishedForTests?.Invoke();
            var candidates = new List<Key>();
            foreach (var source in SupportedSources.Where(s => filters.Source == "all" || filters.Source == s))
            {
                var keys = Select(connection, transaction, source, sid, session, filters, state, limit + 1);
                CandidatesSelectedForTests?.Invoke(source, keys.Count);
                candidates.AddRange(keys);
            }
            candidates.Sort(KeyComparer.Instance);
            var hasMore = candidates.Count > limit;
            var selected = candidates.Take(limit).ToArray();
            var entries = new List<UnifiedHistoryEntry>();
            var ordinaryRows=new List<OrdinaryRecordingHistorySnapshot>();
            var futureRows = new List<FutureWindowAuthorizationRow>();
            var planRows = new List<PlanMediaPublicationRow>();
            foreach (var key in selected)
            {
                entries.Add(key.Source switch
                {
                    "ordinary" => ReadOrdinary(connection, transaction, key, false,ordinaryRows),
                    "plan" => ReadPlan(connection, transaction, key, sid, session, planRows),
                    _ => ReadFuture(connection, transaction, key, sid, session, futureRows),
                });
            }
            foreach (var group in selected.GroupBy(k => k.Source)) EntriesValidatedForTests?.Invoke(group.Key, group.Count());
            var next = hasMore && selected.Length > 0 ? Encode(state with
            { LastTicks = selected[^1].Ticks, LastSource = selected[^1].Source, LastId = selected[^1].Id }) : null;
            PlanMediaPublicationStorage.Read(connection, transaction, planRows, 100, PlanPublicationCommandForTests);
            FutureWindowPublicationStorage.Read(connection, transaction, futureRows, FuturePublicationCommandForTests);
            if (retention is null) PublicationAnchorStorage.Read(connection, transaction, ordinaryRows);
            var bundles = OrdinaryRecordingBundleStorage.Read(connection, transaction, ordinaryRows, BundleContentsCommandForTests, BundleIdentityCommandForTests, MediaHashCommandForTests, AttachmentHashCommandForTests);
            var targets = OrdinaryRecordingTargetStorage.Read(connection, transaction, ordinaryRows, TargetCommandForTests);
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].Source == "ordinary") entries[i] = entries[i] with {
                    Bundle = bundles[entries[i].RecordingId!], TargetKey = targets[entries[i].RecordingId!],
                    OutputFileExists = probeOrdinaryFiles && entries[i].OutputPathRecorded ?
                        (OrdinaryFileProbeForTests ?? FileAccessible)(entries[i].OutputPath!) : null };
            retention?.Invoke(connection,transaction,entries,ordinaryRows);
            transaction.Commit();
            return new(entries, next, limit, filters);
        }
        catch (Exception exception) when (exception is not UnifiedHistoryUnavailableException)
        { throw new UnifiedHistoryUnavailableException(exception); }
    }

    internal sealed record Key(string Source, string Id, long Ticks, long Sequence, string? PlanId = null, string? OwnerKind = null);
    private sealed class KeyComparer : IComparer<Key>
    {
        internal static readonly KeyComparer Instance = new();
        public int Compare(Key? x, Key? y)
        {
            var time = y!.Ticks.CompareTo(x!.Ticks); if (time != 0) return time;
            var source = string.CompareOrdinal(x.Source, y.Source); if (source != 0) return source;
            // SQLite BINARY compares UTF-8 bytes, not UTF-16 code units.
            return Encoding.UTF8.GetBytes(x.Id).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y.Id));
        }
    }

    private List<Key> Select(SqliteConnection connection, SqliteTransaction transaction, string source,
        string sid, string session, UnifiedHistoryFilters filters, Cursor state, int take)
    {
        if (filters.TargetKey is not null) return SelectTarget(connection, transaction, filters, state, take);
        var table = source switch { "ordinary" => "ordinary_recording_history", "plan" => "plan_history_locator", _ => "future_window_authorizations" };
        if (source == "ordinary" && filters.SourceType is not null)
            table += " INDEXED BY " + SqliteSchemaV27.TypeIndexName;
        else if (source == "plan" && filters.PlanId is not null)
            table += " INDEXED BY " + SqliteSchemaV27.PlanIndexName;
        else if (source == "ordinary" && filters.ProfileId is not null)
            table += " INDEXED BY " + (filters.ProfileVersion is null ? SqliteSchemaV26.IdIndexName : SqliteSchemaV26.VersionIndexName);
        var id = source switch { "ordinary" => "recording_id", "plan" => "occurrence_id", _ => "authorization_id" };
        var sequence = source switch { "ordinary" => "history_sequence", "plan" => "occurrence_sequence", _ => "rowid" };
        var watermark = source switch { "ordinary" => state.OrdinaryWatermark, "plan" => state.PlanWatermark, _ => state.FutureWatermark };
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var clauses = new List<string> { $"{sequence}<=$watermark" };
        if (source == "ordinary" && filters.SourceType is { } type)
        { clauses.Add("source_type COLLATE BINARY=$source_type"); Add(command, "$source_type", type); }
        if (source == "plan" && filters.PlanId is { } planId)
        { clauses.Add("plan_id COLLATE BINARY=$plan_id"); Add(command, "$plan_id", planId); }
        if (source == "ordinary" && filters.Status is { } status)
        { clauses.Add("status_code=$status"); Add(command, "$status", status); }
        if (source == "ordinary" && filters.ProfileId is { } profileId)
        {
            clauses.Add("profile_id COLLATE BINARY=$profile_id"); Add(command, "$profile_id", profileId);
            if (filters.ProfileVersion is { } version)
            { clauses.Add("profile_version=$profile_version"); Add(command, "$profile_version", version); }
        }
        if (source != "ordinary") { clauses.Add("current_user_sid=$sid AND session_binding=$session"); Add(command, "$sid", sid); Add(command, "$session", session); }
        if (source == "plan")
            clauses.Add($"EXISTS(SELECT 1 FROM ({SqliteSchemaV23.Owners}) owner WHERE owner.plan_id=(SELECT original.plan_id FROM plan_occurrences original WHERE original.rowid=plan_history_locator.occurrence_sequence) AND owner.current_user_sid=$sid AND owner.session_binding=$session)");
        bool future = source == "future_window";
        // Future timestamps are Unix milliseconds; other sources use UTC ticks.
        long Unit(long ticks) => future ? (ticks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond : ticks;
        long CeilingUnit(long ticks) => future ? (long)Math.Ceiling((decimal)(ticks - UnixEpochTicks) / TimeSpan.TicksPerMillisecond) : ticks;
        if (filters.CreatedFrom is { } from) { clauses.Add("created_at_utc >= $from"); Add(command, "$from", CeilingUnit(from.UtcTicks)); }
        if (filters.CreatedBefore is { } before) { clauses.Add("created_at_utc < $before"); Add(command, "$before", CeilingUnit(before.UtcTicks)); }
        if (state.LastTicks is { } ticks)
        {
            var equalTime = !future || (ticks - UnixEpochTicks) % TimeSpan.TicksPerMillisecond == 0;
            var comparison = string.CompareOrdinal(source, state.LastSource);
            if (equalTime && comparison == 0)
            {
                // Two independent seeks, each capped. UNION ALL lets SQLite merge
                // their ordered indexes without scanning from the index start.
                Add(command, "$last_time", Unit(ticks)); Add(command, "$last_id", state.LastId!);
                var common = string.Join(" AND ", clauses);
                var columns = $"{id}, created_at_utc, {sequence}" + (source == "plan" ? ", plan_id, owner_kind" : "");
                command.CommandText = $"""
                    SELECT {columns} FROM {table} WHERE {common} AND created_at_utc=$last_time AND {id} COLLATE BINARY > $last_id
                    UNION ALL
                    SELECT {columns} FROM {table} WHERE {common} AND created_at_utc < $last_time
                    ORDER BY created_at_utc DESC, {id} COLLATE BINARY ASC LIMIT $take;
                    """;
            }
            else
            {
                // Source order determines whether equal timestamps follow the last key.
                clauses.Add("created_at_utc " + (equalTime && comparison > 0 ? "<=" : "<") + " $last_time");
                Add(command, "$last_time", future && !equalTime ? CeilingUnit(ticks) : Unit(ticks));
            }
        }
        if (string.IsNullOrEmpty(command.CommandText))
            command.CommandText = $"SELECT {id}, created_at_utc, {sequence}" + (source == "plan" ? ", plan_id, owner_kind" : "") +
                $" FROM {table} WHERE {string.Join(" AND ", clauses)} ORDER BY created_at_utc DESC, {id} COLLATE BINARY ASC LIMIT $take;";
        Add(command, "$watermark", watermark); Add(command, "$take", take);
        CandidateCommandForTests?.Invoke(source, command.CommandText,
            command.Parameters.Cast<SqliteParameter>().Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        var keys = new List<Key>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var localId = reader.GetString(0);
            if (!ValidId(localId)) throw new InvalidDataException("Invalid selected history identity.");
            var time = reader.GetInt64(1);
            keys.Add(new(source, localId, future ? checked(time * TimeSpan.TicksPerMillisecond + UnixEpochTicks) : time,
                reader.GetInt64(2), source == "plan" ? reader.GetString(3) : null,
                source == "plan" ? reader.GetString(4) : null));
        }
        return keys;
    }

    private List<Key> SelectTarget(SqliteConnection connection, SqliteTransaction transaction, UnifiedHistoryFilters filters, Cursor state, int take)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var clauses = new List<string> { "t.target_key=$target", "h.history_sequence<=$watermark" };
        void Filter(string sql, string parameter, object? value)
        { if (value is not null) { clauses.Add(sql); Add(command, parameter, value); } }
        Filter("h.status_code=$status", "$status", filters.Status);
        Filter("h.source_type COLLATE BINARY=$source_type", "$source_type", filters.SourceType);
        Filter("h.profile_id COLLATE BINARY=$profile_id", "$profile_id", filters.ProfileId);
        Filter("h.profile_version=$profile_version", "$profile_version", filters.ProfileVersion);
        Filter("t.created_at_utc >= $from", "$from", filters.CreatedFrom?.UtcTicks);
        Filter("t.created_at_utc < $before", "$before", filters.CreatedBefore?.UtcTicks);
        Add(command, "$target", filters.TargetKey!); Add(command, "$watermark", state.OrdinaryWatermark); Add(command, "$take", take);
        var window = OrdinaryWindowTargetKey.IsValid(filters.TargetKey);
        var select = "SELECT t.recording_id,t.created_at_utc,h.history_sequence FROM " +
            (window ? SqliteSchemaV34.Table : SqliteSchemaV32.Table) + " t INDEXED BY " +
            (window ? SqliteSchemaV34.IndexName : SqliteSchemaV32.IndexName) + " JOIN ordinary_recording_history h ON h.recording_id=t.recording_id WHERE " + string.Join(" AND ", clauses);
        command.CommandText = state.LastTicks is null ? select :
            select + " AND t.created_at_utc=$last_time AND t.recording_id COLLATE BINARY > $last_id UNION ALL " +
            select + " AND t.created_at_utc < $last_time";
        command.CommandText += " ORDER BY t.created_at_utc DESC,t.recording_id COLLATE BINARY ASC LIMIT $take;";
        if (state.LastTicks is { } ticks) { Add(command, "$last_time", ticks); Add(command, "$last_id", state.LastId!); }
        CandidateCommandForTests?.Invoke("ordinary", command.CommandText, command.Parameters.Cast<SqliteParameter>()
            .Select(p => new KeyValuePair<string, object>(p.ParameterName, p.Value!)).ToArray());
        var keys = new List<Key>(); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0); if (!ValidId(id)) throw new InvalidDataException("Invalid target candidate identity.");
            keys.Add(new("ordinary", id, reader.GetInt64(1), reader.GetInt64(2)));
        }
        return keys;
    }

    private UnifiedHistoryEntry ReadOrdinary(SqliteConnection connection, SqliteTransaction transaction, Key key, bool probeFiles,List<OrdinaryRecordingHistorySnapshot>? retentionRows=null)
    {
        var row = SqliteOrdinaryRecordingHistoryStore.ReadById(connection, transaction, key.Id, readTarget: false)
            ?? throw new InvalidDataException("The selected ordinary recording disappeared.");
        SqliteOrdinaryRecordingHistoryStore.ValidateSnapshot(retentionRows is null?row:row with{NestedRole=null});
        retentionRows?.Add(row);
        if (row.CreatedAtUtc.UtcTicks != key.Ticks) throw new InvalidDataException("Ordinary history creation time disagrees.");
        // Successful active stops use the existing completed publication path.
        var evidence = row.Status == "completed" && row.OutputSizeBytes is > 0 && row.OutputDurationSeconds is > 0 && row.CompletedAtUtc is not null;
        if (evidence) ValidatePath(row.OutputPath);
        return new("ordinary:" + row.RecordingId, "ordinary", "ordinary_video", row.CreatedAtUtc,
            RecordingId: row.RecordingId, Status: row.Status, ReasonCode: row.ReasonCode,
            OutputPath: evidence ? row.OutputPath : null, OutputPathRecorded: evidence,
            OutputFileExists: evidence && probeFiles ? (OrdinaryFileProbeForTests ?? FileAccessible)(row.OutputPath) : null,
            OutputSizeBytes: evidence ? row.OutputSizeBytes : null, OutputDurationSeconds: evidence ? row.OutputDurationSeconds : null,
            StartedAtUtc: row.StartedAtUtc, CompletedAtUtc: row.CompletedAtUtc,
            ProfileId: row.ProfileId, ProfileVersion: row.ProfileVersion, SourceType: row.SourceType);
    }

    internal static UnifiedHistoryEntry ReadPlan(SqliteConnection connection, SqliteTransaction transaction, Key key, string sid, string session, List<PlanMediaPublicationRow> publicationRows, bool probeFiles = true)
    {
        using (var relation = connection.CreateCommand())
        {
            relation.Transaction = transaction;
            relation.CommandText = $"""
                SELECT COUNT(*) FROM plan_occurrences o JOIN ({SqliteSchemaV23.Owners}) c ON c.plan_id=o.plan_id
                WHERE o.id=$id AND o.plan_id=$plan AND o.rowid=$sequence AND o.created_at_utc=$time
                  AND c.current_user_sid=$sid AND c.session_binding=$session AND c.owner_kind=$kind;
                """;
            Add(relation, "$id", key.Id); Add(relation, "$plan", key.PlanId!); Add(relation, "$sequence", key.Sequence);
            Add(relation, "$time", key.Ticks); Add(relation, "$sid", sid); Add(relation, "$session", session); Add(relation, "$kind", key.OwnerKind!);
            if (relation.ExecuteScalar() is not long count || count != 1) throw new InvalidDataException("The directory and original owner disagree.");
        }
        var row = PlanExecutionStatusQueryService.ReadUnifiedOccurrence(connection, transaction, key.PlanId!, key.Id, sid, session, publicationRows, probeFiles);
        if (row.CreatedAtUtc.UtcTicks != key.Ticks) throw new InvalidDataException("The occurrence creation time disagrees.");
        return new("plan:" + row.OccurrenceId, "plan", "plan_occurrence", row.CreatedAtUtc,
            PlanId: key.PlanId, OccurrenceId: row.OccurrenceId, RunId: row.RunId,
            Status: row.Status, ReasonCode: row.TerminalReasonCode, OccurrenceStatus: row.Status,
            RunStatus: row.Run?.Status, RunReasonCode: row.Run?.TerminalReasonCode, ExecutionStatusCode: row.ExecutionStatusCode,
            OutputPath: row.OutputPath, OutputPathRecorded: row.OutputPathRecorded, OutputFileExists: row.OutputFileExists,
            WindowStartUtc: row.WindowStartUtc, WindowEndUtc: row.WindowEndUtc);
    }

    internal Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? PlanPublicationCommandForTests { get; set; }

    internal Action<SqliteConnection, SqliteTransaction, string, IReadOnlyList<KeyValuePair<string, object>>>? FuturePublicationCommandForTests { get; set; }

    private static UnifiedHistoryEntry ReadFuture(SqliteConnection connection, SqliteTransaction transaction, Key key, string sid, string session, List<FutureWindowAuthorizationRow> rows)
    {
        var row = SqliteFutureWindowAuthorizationRepository.ReadById(connection, transaction, key.Id, sid, session)
            ?? throw new InvalidDataException("The selected authorization disappeared.");
        FutureHistoryValidation.Validate(row);
        rows.Add(row);
        if (row.CreatedAtUtc.UtcTicks != key.Ticks) throw new InvalidDataException("The authorization creation time disagrees.");
        var evidence = row.StatusCode == "completed";
        return new("future_window:" + row.AuthorizationId, "future_window", "future_window_authorization", row.CreatedAtUtc,
            AuthorizationId: row.AuthorizationId, RunId: row.RunId, Status: row.StatusCode, ReasonCode: row.ReasonCode,
            AuthorizationStatus: row.StatusCode, RunStatus: row.RunStatus,
            RunReasonCode: row.RunId is null ? null : row.ReasonCode,
            OutputPath: evidence ? row.OutputPath : null, OutputPathRecorded: evidence,
            OutputFileExists: evidence ? FileAccessible(row.OutputPath!) : null,
            OutputSizeBytes: evidence ? row.OutputSizeBytes : null,
            OutputDurationSeconds: evidence ? row.ActualDurationMs / 1000d : null);
    }

    internal static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path != Path.GetFullPath(path)) throw new InvalidDataException("Invalid persistent path.");
    }
    internal static bool FileAccessible(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }
    private static long Max(SqliteConnection c, SqliteTransaction t, string table, string column)
    { using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = $"SELECT COALESCE(MAX({column}),0) FROM {table};"; return (long)command.ExecuteScalar()!; }
    private static void Add(SqliteCommand c, string name, object value) => c.Parameters.AddWithValue(name, value);
    internal static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value == value.Trim() && !value.Contains('\0');
    private static bool ValidIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && value == value.Trim() && !value.Contains('\0');
    public static void ValidateArguments(int limit, UnifiedHistoryFilters filters)
    {
        if (filters is null || !filters.HasValidStatus || !filters.HasValidProfile || !filters.HasValidSourceType || !filters.HasValidPlanId || !filters.HasValidTargetKey || limit is < 1 or > 100 || filters.Source is not ("all" or "ordinary" or "plan" or "future_window") ||
            filters.CreatedFrom is { Offset: var f } && f != TimeSpan.Zero || filters.CreatedBefore is { Offset: var b } && b != TimeSpan.Zero ||
            filters.CreatedFrom is { } from && filters.CreatedBefore is { } before && from >= before)
            throw new UnifiedHistoryArgumentException();
    }
    private sealed record Cursor(int Version, string Identity, UnifiedHistoryFilters Filters,
        long OrdinaryWatermark, long PlanWatermark, long FutureWatermark, long? LastTicks, string? LastSource, string? LastId);
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Encode(Cursor state)
    {
        var body = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(state, Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var cursor = body + "." + Digest(body);
        if (cursor.Length > 2048) throw new InvalidDataException("History cursor exceeds its size bound.");
        return cursor;
    }
    private static Cursor Decode(string cursor)
    {
        try
        {
            if (cursor.Length is 0 or > 2048) throw new FormatException();
            var parts = cursor.Split('.'); if (parts.Length != 2 || parts[1] != Digest(parts[0])) throw new FormatException();
            var b64 = parts[0].Replace('-', '+').Replace('_', '/'); b64 += new string('=', (4 - b64.Length % 4) % 4);
            var state = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(b64), Json) ?? throw new FormatException();
            if (state.Filters is null || state.LastTicks is null || state.LastSource is null || state.LastId is null) throw new FormatException();
            return state;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { throw new UnifiedHistoryArgumentException(); }
    }
}
