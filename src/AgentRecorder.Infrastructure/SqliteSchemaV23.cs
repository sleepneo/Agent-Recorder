using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>An identity/time seek directory, never an authorization or lifecycle store.</summary>
internal static class SqliteSchemaV23
{
    public const string MigrationName = "schema_v23_indexed_unified_history";
    public const string MigrationChecksum = "38f9eb5e64665c42beaf366187359b6bded9c4f5bf409f6143ef2b08b7887d07";
    internal const string Owners = """
        SELECT plan_id, 'standing_once' AS owner_kind, current_user_sid, session_binding
        FROM setup_intents WHERE plan_id IS NOT NULL AND (intent_kind_code='standing_once_fixed_region'
            OR EXISTS(SELECT 1 FROM plans owner_plan WHERE owner_plan.id=setup_intents.plan_id AND owner_plan.is_one_time=1))
        UNION ALL
        SELECT plan_id, 'recurring', current_user_sid, session_binding FROM recurring_lease_local_approvals
        UNION ALL
        SELECT plan_id, 'required_once', current_user_sid, session_binding
        FROM required_once_setup_intents WHERE plan_id IS NOT NULL
        """;

    private static readonly IReadOnlyList<(string Name, string Table, string Sql)> TriggerSql = BuildTriggers();
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = new[] { CreateMigration() };
    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = new[]
    {
        new SqliteTableDefinition("plan_history_locator", new[]
        {
            new SqliteColumnDefinition("occurrence_sequence", "INTEGER", true, 1),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("owner_kind", "TEXT", false, 0),
            new SqliteColumnDefinition("current_user_sid", "TEXT", false, 0),
            new SqliteColumnDefinition("session_binding", "TEXT", false, 0),
        }),
    };
    private const string FutureIndex = "CREATE INDEX idx_future_window_history_identity_created ON future_window_authorizations(current_user_sid, session_binding, created_at_utc DESC, authorization_id COLLATE BINARY ASC);";
    private const string PlanIndex = "CREATE INDEX idx_plan_history_identity_created ON plan_history_locator(current_user_sid, session_binding, created_at_utc DESC, occurrence_id COLLATE BINARY ASC);";
    private const string SetupIndex = "CREATE INDEX idx_setup_intents_history_plan ON setup_intents(plan_id);";
    private const string ApprovalIndex = "CREATE INDEX idx_recurring_approvals_history_plan ON recurring_lease_local_approvals(plan_id);";
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = new[]
    {
        Index("idx_future_window_history_identity_created", "future_window_authorizations", new[] { "current_user_sid", "session_binding", "created_at_utc", "authorization_id" }, FutureIndex),
        Index("idx_plan_history_identity_created", "plan_history_locator", new[] { "current_user_sid", "session_binding", "created_at_utc", "occurrence_id" }, PlanIndex),
        Index("idx_setup_intents_history_plan", "setup_intents", new[] { "plan_id" }, SetupIndex),
        Index("idx_recurring_approvals_history_plan", "recurring_lease_local_approvals", new[] { "plan_id" }, ApprovalIndex),
    };
    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = new[]
    { new SqliteUniqueConstraintDefinition("plan_history_locator", new[] { "occurrence_id" }) };
    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = new[]
    {
        new SqliteCheckConstraintDefinition("plan_history_locator", new Regex("CHECK \\(occurrence_sequence > 0\\)", RegexOptions.IgnoreCase)),
        new SqliteCheckConstraintDefinition("plan_history_locator", new Regex("owner_kind IS NULL AND current_user_sid IS NULL AND session_binding IS NULL", RegexOptions.IgnoreCase)),
    };
    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = TriggerSql.Select(t =>
        new SqliteTriggerDefinition(t.Name, t.Table, Array.Empty<Regex>(), Exact(t.Sql))).ToArray();

    private static Regex Exact(string sql) => new("^" + Regex.Escape(Regex.Replace(sql, @"\s+", " ").Trim().TrimEnd(';')) + ";?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static SqliteIndexDefinition Index(string name, string table, string[] columns, string sql) =>
        new(name, table, false, columns, DefinitionPattern: Exact(sql));

    private static string Refresh(string predicate, string? selection = null) => $"""
        SELECT RAISE(ABORT, 'history_owner_ambiguous') WHERE EXISTS (
            SELECT c.plan_id FROM ({Owners}) c JOIN plans p ON p.id=c.plan_id
            WHERE {predicate.Replace("o.", "p.", StringComparison.Ordinal).Replace("p.plan_id", "p.id", StringComparison.Ordinal)}
            GROUP BY c.plan_id HAVING COUNT(*) != 1);
        SELECT RAISE(ABORT, 'history_owner_invalid') WHERE EXISTS (
            SELECT 1 FROM ({Owners}) c JOIN plans p ON p.id=c.plan_id
            WHERE ({predicate.Replace("o.", "p.", StringComparison.Ordinal).Replace("p.plan_id", "p.id", StringComparison.Ordinal)})
              AND (c.owner_kind='recurring') != (p.is_one_time=0));
        INSERT INTO plan_history_locator(occurrence_sequence, occurrence_id, plan_id, created_at_utc, owner_kind, current_user_sid, session_binding)
        SELECT o.rowid, o.id, o.plan_id, o.created_at_utc, c.owner_kind, c.current_user_sid, c.session_binding
        FROM plan_occurrences o LEFT JOIN ({Owners}) c ON c.plan_id=o.plan_id
        WHERE {selection ?? predicate}
        ON CONFLICT(occurrence_id) DO UPDATE SET
            occurrence_sequence=excluded.occurrence_sequence, plan_id=excluded.plan_id,
            created_at_utc=excluded.created_at_utc, owner_kind=excluded.owner_kind,
            current_user_sid=excluded.current_user_sid, session_binding=excluded.session_binding;
        """;

    private static IReadOnlyList<(string Name, string Table, string Sql)> BuildTriggers()
    {
        var list = new List<(string, string, string)>();
        void Add(string name, string table, string timing, string body, string when = "") =>
            list.Add((name, table, $"CREATE TRIGGER {name} {timing} ON {table} {when} BEGIN {body} END;"));
        // Candidate inserts/updates must mirror the original occurrence and the
        // actual owner relation. A locator cannot create its own authority.
        var valid = $"""
            SELECT RAISE(ABORT, 'history_locator_invalid') WHERE NOT EXISTS (
                SELECT 1 FROM plan_occurrences o WHERE o.rowid=NEW.occurrence_sequence
                  AND o.id=NEW.occurrence_id AND o.plan_id=NEW.plan_id AND o.created_at_utc=NEW.created_at_utc)
              OR (SELECT COUNT(*) FROM ({Owners}) c WHERE c.plan_id=NEW.plan_id) > 1
              OR NOT (
                (NEW.owner_kind IS NULL AND NOT EXISTS(SELECT 1 FROM ({Owners}) c WHERE c.plan_id=NEW.plan_id))
                OR EXISTS(SELECT 1 FROM ({Owners}) c WHERE c.plan_id=NEW.plan_id
                  AND c.owner_kind=NEW.owner_kind AND c.current_user_sid=NEW.current_user_sid AND c.session_binding=NEW.session_binding));
            """;
        Add("trg_history_locator_insert_guard", "plan_history_locator", "BEFORE INSERT", valid);
        Add("trg_history_locator_update_guard", "plan_history_locator", "BEFORE UPDATE", valid);
        Add("trg_history_locator_delete_guard", "plan_history_locator", "BEFORE DELETE",
            "SELECT RAISE(ABORT, 'history_locator_required') WHERE EXISTS(SELECT 1 FROM plan_occurrences WHERE id=OLD.occurrence_id);");
        // For occurrence predicates the INSERT guard detects ambiguous owners,
        // avoiding a Plan/occurrence identifier mix-up in the generic refresh.
        var occurrenceRefresh = Refresh("o.plan_id=NEW.plan_id", "o.id=NEW.id");
        Add("trg_history_occurrence_insert", "plan_occurrences", "AFTER INSERT", occurrenceRefresh);
        Add("trg_history_occurrence_update", "plan_occurrences", "AFTER UPDATE OF id, plan_id, created_at_utc",
            "DELETE FROM plan_history_locator WHERE occurrence_id=OLD.id AND OLD.id!=NEW.id; " + occurrenceRefresh,
            "WHEN OLD.id IS NOT NEW.id OR OLD.plan_id IS NOT NEW.plan_id OR OLD.created_at_utc IS NOT NEW.created_at_utc");
        Add("trg_history_occurrence_delete", "plan_occurrences", "AFTER DELETE", "DELETE FROM plan_history_locator WHERE occurrence_id=OLD.id;");
        foreach (var (table, fields) in new[]
        {
            ("setup_intents", "plan_id, current_user_sid, session_binding, intent_kind_code"),
            ("required_once_setup_intents", "plan_id, current_user_sid, session_binding"),
            ("recurring_lease_local_approvals", "plan_id, current_user_sid, session_binding"),
        })
        {
            var prefix = "trg_history_" + table;
            Add(prefix + "_insert", table, "AFTER INSERT", Refresh("o.plan_id=NEW.plan_id"), "WHEN NEW.plan_id IS NOT NULL");
            var changed = string.Join(" OR ", fields.Split(", ").Select(f => $"OLD.{f} IS NOT NEW.{f}"));
            Add(prefix + "_update", table, "AFTER UPDATE OF " + fields,
                Refresh("o.plan_id=OLD.plan_id OR o.plan_id=NEW.plan_id"), "WHEN " + changed);
            Add(prefix + "_delete", table, "AFTER DELETE", Refresh("o.plan_id=OLD.plan_id"), "WHEN OLD.plan_id IS NOT NULL");
        }
        return list;
    }

    internal static string ConsistencyFailures => $"""
        SELECT 1 FROM plan_occurrences o LEFT JOIN plan_history_locator l ON l.occurrence_id=o.id
        LEFT JOIN ({Owners}) c ON c.plan_id=o.plan_id
        WHERE l.occurrence_id IS NULL OR l.occurrence_sequence!=o.rowid OR l.plan_id!=o.plan_id
          OR l.created_at_utc!=o.created_at_utc OR l.owner_kind IS NOT c.owner_kind
          OR l.current_user_sid IS NOT c.current_user_sid OR l.session_binding IS NOT c.session_binding
        UNION ALL
        SELECT 1 FROM plan_history_locator l WHERE NOT EXISTS(SELECT 1 FROM plan_occurrences o WHERE o.id=l.occurrence_id)
        UNION ALL
        SELECT 1 FROM ({Owners}) c LEFT JOIN plans p ON p.id=c.plan_id
        WHERE p.id IS NULL OR (c.owner_kind='recurring') != (p.is_one_time=0)
        UNION ALL
        SELECT 1 FROM ({Owners}) c GROUP BY c.plan_id HAVING COUNT(*)>1
        UNION ALL
        SELECT 1 FROM setup_intents s JOIN plans p ON p.id=s.plan_id
        WHERE p.is_one_time=1 AND s.intent_kind_code!='standing_once_fixed_region'
        """;

    private static SqliteMigrationDefinition CreateMigration()
    {
        var sql = $"""
            {FutureIndex}
            {SetupIndex}
            {ApprovalIndex}
            CREATE TABLE plan_history_locator (
                occurrence_sequence INTEGER NOT NULL PRIMARY KEY CHECK (occurrence_sequence > 0),
                occurrence_id TEXT NOT NULL UNIQUE COLLATE BINARY,
                plan_id TEXT NOT NULL COLLATE BINARY,
                created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
                owner_kind TEXT NULL CHECK (owner_kind IS NULL OR owner_kind IN ('standing_once','recurring','required_once')),
                current_user_sid TEXT NULL,
                session_binding TEXT NULL,
                CHECK ((owner_kind IS NULL AND current_user_sid IS NULL AND session_binding IS NULL) OR
                    (owner_kind IS NOT NULL AND current_user_sid IS NOT NULL AND session_binding IS NOT NULL
                      AND length(trim(current_user_sid)) BETWEEN 1 AND 256 AND current_user_sid=trim(current_user_sid)
                      AND length(trim(session_binding)) BETWEEN 1 AND 256 AND session_binding=trim(session_binding)))
            );
            {PlanIndex}
            INSERT INTO plan_history_locator(occurrence_sequence, occurrence_id, plan_id, created_at_utc, owner_kind, current_user_sid, session_binding)
            SELECT o.rowid, o.id, o.plan_id, o.created_at_utc, c.owner_kind, c.current_user_sid, c.session_binding
            FROM plan_occurrences o LEFT JOIN ({Owners}) c ON c.plan_id=o.plan_id;
            CREATE TEMP TABLE task311r_migration_guard (valid INTEGER NOT NULL CHECK(valid=1));
            INSERT INTO task311r_migration_guard(valid) SELECT CASE WHEN EXISTS({ConsistencyFailures}) THEN 0 ELSE 1 END;
            DROP TABLE task311r_migration_guard;
            {string.Join("\n", TriggerSql.Select(t => t.Sql))}
            """;
        var canonical = SqliteSchemaV1.CanonicalizeDefinition(sql);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonical);
        if (checksum != MigrationChecksum)
            throw new InvalidOperationException($"The fixed schema v23 definition checksum is {checksum}.");
        return new(23, MigrationName, canonical, checksum);
    }
}
