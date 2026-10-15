using AgentRecorder.Core;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;
using AgentRecorder.Windows;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Tests;

internal sealed class UnifiedHistoryTestDatabase : IDisposable
{
    internal const string Sid = "S-1-5-21-history-test";
    internal const string Session = "history-session";
    internal static readonly DateTimeOffset At = new(2026, 10, 5, 1, 0, 0, TimeSpan.Zero);
    internal string Root { get; } = Path.GetFullPath(Path.Combine(".local-data/temp/task311r", Guid.NewGuid().ToString("N")));
    internal SqliteOperationalStore Store { get; }
    internal SqliteOrdinaryRecordingHistoryStore Ordinary { get; }
    internal UnifiedHistoryQueryService Query { get; }
    internal SqliteFutureWindowAuthorizationRepository Future { get; }
    internal UnifiedHistoryTestDatabase(string? root = null)
    {
        if (root is not null) Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Store = new(Path.Combine(Root, "history.db")); Store.Initialize();
        Ordinary = new(Store); Query = new(Store, Ordinary); Future = new(Store);
        Execute("UPDATE unattended_safety_state SET unattended_mode_code='enabled';");
    }
    internal UnifiedHistoryPage Page(int limit = 20, UnifiedHistoryFilters? filters = null, string? cursor = null,
        string sid = Sid, string session = Session) => Query.GetPage(sid, session, limit, filters ?? new(), cursor);
    internal OrdinaryRecordingHistorySnapshot Recording(string id, DateTimeOffset? at = null, bool completed = false)
    {
        var time = at ?? At;
        return new(id, time, completed ? "completed" : "preparing", "display", "display_capture", "Synthetic source",
            15, "none", null, null, null, null, Path.Combine(Root, id + ".mp4"), null, null, null, null, null, null,
            completed ? time.AddSeconds(1) : null, completed ? time.AddSeconds(16) : null,
            completed ? "duration_reached" : null, completed ? 12 : null, completed ? 15 : null);
    }
    internal void SeedOrdinary(string id, DateTimeOffset? at = null, bool completed = false) => Ordinary.Register(Recording(id, at, completed));
    // Scale fixtures model scheduled occurrences only, not real executions.
    // They retain all schema constraints and trigger-maintained locator writes.
    internal void SeedPlan(string id, string sid = Sid, string session = Session, bool owner = true)
    {
        new SqlitePlanDefinitionRepository(Store).Insert(new PlanDefinition(id, true, At));
        if (owner) BindPlan(id, sid, session);
    }
    internal void BindPlan(string id, string sid = Sid, string session = Session, string? intent = null)
    {
        Execute("""
            INSERT INTO setup_intents(intent_id,intent_kind_code,idempotency_key,request_digest,current_user_sid,
                session_binding,status_code,requested_at_utc,expires_at_utc,plan_id,created_at_utc,updated_at_utc,version)
            VALUES($intent,'standing_once_fixed_region',$intent,$digest,$sid,$session,'activated',$at,$end,$plan,$at,$at,0);
            """, ("$intent", intent ?? "setup-" + id), ("$digest", new string('a', 64)), ("$sid", sid), ("$session", session),
            ("$at", At.UtcTicks), ("$end", At.AddDays(1).UtcTicks), ("$plan", id));
    }
    internal void SeedOccurrence(string planId, string id, DateTimeOffset? at = null)
    {
        var time = at ?? At;
        new SqlitePlanOccurrenceRepository(Store).Insert(new PlanOccurrence(id, planId, time, time.AddMinutes(1), time));
    }
    internal FutureWindowAuthorizationScope SeedFuture(string id, DateTimeOffset? at = null, string sid = Sid, string session = Session, string? outputPath = null)
    {
        var scope = new FutureWindowAuthorizationScope(id, "key-" + id, new string('a', 64), sid, session,
            new FutureWindowExecutableIdentity(1, @"C:\Nonexistent\Player.exe", "fixture-file-identity", new string('a', 64), null, null),
            null, null, 60, 300, outputPath is null ? Root : Path.GetDirectoryName(outputPath)!, outputPath is null ? id + ".mp4" : Path.GetFileName(outputPath), "pending", at ?? At, null, null, null, null, 0);
        Future.CreateOrGet(scope); return scope;
    }
    internal FutureWindowStartCommitReceipt StartFuture(FutureWindowAuthorizationScope scope, int seconds = 2)
    {
        Future.Approve(scope.AuthorizationId, scope.CurrentUserSid, scope.SessionBinding, "approval-" + scope.AuthorizationId, scope.CreatedAtUtc.AddSeconds(1));
        var now = scope.CreatedAtUtc.AddSeconds(seconds);
        var process = new FutureWindowProcessSnapshot("window-test", new nint(42), 43, now.UtcDateTime.ToFileTimeUtc(),
            scope.CurrentUserSid, 1, scope.ExecutableIdentity.CanonicalPath, scope.ExecutableIdentity);
        return Future.TryCommitStart(scope.AuthorizationId, scope.CurrentUserSid, scope.SessionBinding, process, now, out _)!
            ?? throw new InvalidOperationException("Fixture start failed.");
    }
    internal void Execute(string sql, params (string Name, object Value)[] args)
    { using var c = Store.OpenConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; foreach (var a in args) cmd.Parameters.AddWithValue(a.Name, a.Value); cmd.ExecuteNonQuery(); }
    internal void Corrupt(string sql, params (string Name, object Value)[] args)
    {
        // Dedicated disposable corruption fixture. Production constraints remain
        // unchanged; restore exact triggers before exercising the query.
        using var c = Store.OpenConnection(); var triggers = new List<(string Name, string Sql)>();
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger';"; using var r = cmd.ExecuteReader(); while(r.Read()) triggers.Add((r.GetString(0),r.GetString(1))); }
        using var command = c.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON;"; command.ExecuteNonQuery();
        foreach(var t in triggers) { command.CommandText = $"DROP TRIGGER \"{t.Name}\";"; command.ExecuteNonQuery(); }
        try { command.CommandText = sql; foreach(var a in args) command.Parameters.AddWithValue(a.Name,a.Value); command.ExecuteNonQuery(); }
        finally { command.Parameters.Clear(); foreach(var t in triggers) { command.CommandText=t.Sql; command.ExecuteNonQuery(); } command.CommandText="PRAGMA ignore_check_constraints=OFF; PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery(); }
    }
    internal void DowngradeToV22()
    {
        DropPublicationReferenceMigration();
        // This owned migration fixture has deliberately removed v42 already.
        // A current-schema business connection must continue to reject it.
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Store.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open(); SqliteOutputPathCollation.Register(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;"; cmd.ExecuteNonQuery();
        cmd.CommandText="DROP TRIGGER plan_media_publication_initial; DROP TABLE plan_media_publication_anchors; DROP TABLE plan_media_publication_boundary; DELETE FROM schema_migrations WHERE version=41; DROP TRIGGER future_window_publication_initial; DROP TABLE future_window_publication_anchors; DROP TABLE future_window_publication_boundary; DELETE FROM schema_migrations WHERE version=40;";cmd.ExecuteNonQuery();
        foreach(var index in SqliteSchemaV39.Indexes) {cmd.CommandText="DROP INDEX "+index.Name+";";cmd.ExecuteNonQuery();}
        cmd.CommandText="DELETE FROM schema_migrations WHERE version=39;";cmd.ExecuteNonQuery();
        foreach(var index in SqliteSchemaV38.Indexes) {cmd.CommandText="DROP INDEX "+index.Name+";";cmd.ExecuteNonQuery();}
        cmd.CommandText="DELETE FROM schema_migrations WHERE version=38;";cmd.ExecuteNonQuery();
        cmd.CommandText="DROP TRIGGER ordinary_bundle_attachment_hash_initial; DROP TABLE ordinary_bundle_attachment_hash_items; DROP TABLE ordinary_bundle_attachment_hash; DROP TABLE ordinary_bundle_attachment_hash_boundary; DELETE FROM schema_migrations WHERE version=37; DROP TRIGGER ordinary_media_hash_initial; DROP INDEX idx_ordinary_media_hash_recording; DROP TABLE ordinary_media_hash; DROP TABLE ordinary_media_hash_boundary; DELETE FROM schema_migrations WHERE version=36; DROP TRIGGER ordinary_bundle_identity_initial; DROP TABLE ordinary_bundle_identity_items; DROP TABLE ordinary_bundle_identity; DROP TABLE ordinary_bundle_identity_boundary; DELETE FROM schema_migrations WHERE version=35; DROP TRIGGER ordinary_window_target_initial; DROP TABLE ordinary_recording_window_targets; DROP TABLE ordinary_window_target_boundary; DELETE FROM schema_migrations WHERE version=34; DROP TRIGGER ordinary_bundle_contents_initial; DROP TABLE ordinary_bundle_contents_items; DROP TABLE ordinary_bundle_contents; DROP TABLE ordinary_bundle_contents_boundary; DELETE FROM schema_migrations WHERE version=33; DROP TRIGGER ordinary_recording_target_initial; DROP TABLE ordinary_recording_targets; DROP TABLE ordinary_recording_target_boundary; DELETE FROM schema_migrations WHERE version=32; DROP TABLE ordinary_recording_bundles; DELETE FROM schema_migrations WHERE version=31; DROP TABLE ordinary_publication_anchors; DELETE FROM schema_migrations WHERE version=30;";cmd.ExecuteNonQuery();
        foreach(var index in SqliteSchemaV29.Indexes){cmd.CommandText=$"DROP INDEX {index.Name};";cmd.ExecuteNonQuery();}
        cmd.CommandText="DELETE FROM schema_migrations WHERE version=29;";cmd.ExecuteNonQuery();
        foreach(var index in SqliteSchemaV28.Indexes){cmd.CommandText=$"DROP INDEX {index.Name};";cmd.ExecuteNonQuery();}
        cmd.CommandText="DELETE FROM schema_migrations WHERE version=28; DROP INDEX idx_ordinary_recording_history_type_created; DROP INDEX idx_plan_history_identity_plan_created; DELETE FROM schema_migrations WHERE version=27; DROP INDEX idx_ordinary_recording_history_profile_created; DROP INDEX idx_ordinary_recording_history_profile_version_created; DELETE FROM schema_migrations WHERE version=26; DROP INDEX idx_ordinary_recording_history_parent; DELETE FROM schema_migrations WHERE version=25; DROP INDEX idx_ordinary_recording_history_status_created; DELETE FROM schema_migrations WHERE version=24;"; cmd.ExecuteNonQuery();
        foreach (var trigger in SqliteSchemaV23.Triggers) { cmd.CommandText=$"DROP TRIGGER \"{trigger.Name}\";"; cmd.ExecuteNonQuery(); }
        foreach (var index in SqliteSchemaV23.Indexes) { cmd.CommandText=$"DROP INDEX \"{index.Name}\";"; cmd.ExecuteNonQuery(); }
        cmd.CommandText="DROP TABLE plan_history_locator; DELETE FROM schema_migrations WHERE version=23;"; cmd.ExecuteNonQuery();
    }
    internal void RawExecute(string sql)
    { using var c = new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Store.DatabasePath,Pooling=false}.ToString()); c.Open(); using var cmd=c.CreateCommand(); cmd.CommandText=sql; cmd.ExecuteNonQuery(); }
    internal void DropPublicationReferenceMigration()
    {
        // This fixture explicitly creates v41, so first remove v44/v43 too. Use one
        // existing-only connection; current business policy must reject the downgrade.
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Store.DatabasePath,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());c.Open();
        using var cmd=c.CreateCommand();cmd.CommandText="DROP TABLE ordinary_deletion_requests; DELETE FROM schema_migrations WHERE version=44; DROP TABLE ordinary_deletion_operations; DELETE FROM schema_migrations WHERE version=43;";cmd.ExecuteNonQuery();
        foreach(var index in SqliteSchemaV42.Indexes){cmd.CommandText="DROP INDEX "+index.Name+";";cmd.ExecuteNonQuery();}
        cmd.CommandText="DELETE FROM schema_migrations WHERE version=42;";cmd.ExecuteNonQuery();
    }
    internal long Scalar(string sql)
    { using var c = Store.OpenConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!; }
    internal string Snapshot()
    {
        using var c = Store.OpenConnection();
        var tables = new List<string>();
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"; using var r = cmd.ExecuteReader(); while (r.Read()) tables.Add(r.GetString(0)); }
        var rows = new List<string>();
        foreach (var table in tables)
        { using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT * FROM \"{table}\";"; using var r = cmd.ExecuteReader(); while (r.Read()) rows.Add(table + ":" + string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)))); }
        rows.Sort(StringComparer.Ordinal); return string.Join("\n", rows);
    }
    public void Dispose() { Ordinary.Dispose(); SqliteConnection.ClearAllPools(); try { Directory.Delete(Root, true); } catch { } }
}
