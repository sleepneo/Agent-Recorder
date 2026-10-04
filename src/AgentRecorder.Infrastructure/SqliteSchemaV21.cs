using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>V21 adds the durable management directory for immutable region profiles.</summary>
internal static class SqliteSchemaV21
{
    public const string MigrationName = "schema_v21_fixed_region_profile_management";
    public const string MigrationChecksum = "bc9b43603cedb067ce33e62cb4110c5779609a00af17086a631dba5c4573c34b";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[] { CreateMigration() });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("fixed_region_profile_directory", new[]
        {
            new SqliteColumnDefinition("profile_id", "TEXT", true, 1),
            new SqliteColumnDefinition("name", "TEXT", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("deleted_at_utc", "INTEGER", false, 0),
        }),
        new SqliteTableDefinition("fixed_region_profile_create_idempotency", new[]
        {
            new SqliteColumnDefinition("idempotency_key", "TEXT", true, 1),
            new SqliteColumnDefinition("method", "TEXT", true, 0),
            new SqliteColumnDefinition("request_path", "TEXT", true, 0),
            new SqliteColumnDefinition("request_hash", "TEXT", true, 0),
            new SqliteColumnDefinition("profile_id", "TEXT", true, 0),
            new SqliteColumnDefinition("result_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("result_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("result_name", "TEXT", true, 0),
            new SqliteColumnDefinition("result_etag", "TEXT", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition(
            "idx_fixed_region_profile_directory_live_id",
            "fixed_region_profile_directory",
            false,
            new[] { "profile_id" },
            PredicatePattern: new Regex("deleted_at_utc\\s+IS\\s+NULL", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            DefinitionPattern: new Regex("CREATE\\s+INDEX.*ON\\s+fixed_region_profile_directory\\s*\\(\\s*profile_id\\s*\\)\\s+WHERE\\s+deleted_at_utc\\s+IS\\s+NULL", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled)),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.Empty<SqliteUniqueConstraintDefinition>();
    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();
    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("fixed_region_profile_directory", "profile_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(profile_id,.*\\)\\)\\s+BETWEEN\\s+1\\s+AND\\s+256"),
        Check("fixed_region_profile_directory", "name\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(name,.*\\)\\)\\s+BETWEEN\\s+1\\s+AND\\s+80"),
        Check("fixed_region_profile_directory", "updated_at_utc\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(updated_at_utc\\s*>=\\s*created_at_utc\\)"),
        Check("fixed_region_profile_directory", "deleted_at_utc\\s+INTEGER.*CHECK\\s*\\(deleted_at_utc\\s+IS\\s+NULL\\s+OR\\s+deleted_at_utc\\s*>=\\s+created_at_utc\\)"),
        Check("fixed_region_profile_create_idempotency", "idempotency_key\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(idempotency_key\\)\\s+BETWEEN\\s+1\\s+AND\\s+128"),
        Check("fixed_region_profile_create_idempotency", "request_hash\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(request_hash\\)\\s*=\\s*64"),
        Check("fixed_region_profile_create_idempotency", "result_version\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(result_version\\s*=\\s+1\\)"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTriggerDefinition(
            "trg_fixed_region_profile_directory_insert",
            "recurring_fixed_region_profile_versions",
            Array.Empty<Regex>(),
            new Regex("^CREATE\\s+TRIGGER\\s+trg_fixed_region_profile_directory_insert\\s+AFTER\\s+INSERT\\s+ON\\s+recurring_fixed_region_profile_versions.*fixed_region_profile_directory.*END\\s*;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled)),
        new SqliteTriggerDefinition(
            "trg_recurring_plan_profile_bindings_reject_deleted_profile",
            "recurring_plan_profile_bindings",
            Array.Empty<Regex>(),
            new Regex("^CREATE\\s+TRIGGER\\s+trg_recurring_plan_profile_bindings_reject_deleted_profile\\s+BEFORE\\s+INSERT\\s+ON\\s+recurring_plan_profile_bindings.*deleted_at_utc\\s+IS\\s+NOT\\s+NULL.*END\\s*;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled)),
        ImmutableTrigger("trg_fixed_region_profile_create_idempotency_immutable_update", "UPDATE"),
        ImmutableTrigger("trg_fixed_region_profile_create_idempotency_immutable_delete", "DELETE"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE fixed_region_profile_directory (
            profile_id TEXT NOT NULL PRIMARY KEY COLLATE BINARY CHECK (length(trim(profile_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) BETWEEN 1 AND 256 AND profile_id = trim(profile_id) AND instr(profile_id, char(47)) = 0 AND instr(profile_id, char(92)) = 0),
            name TEXT NOT NULL CHECK (length(trim(name, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) BETWEEN 1 AND 80 AND length(name) <= 160),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            deleted_at_utc INTEGER NULL CHECK (deleted_at_utc IS NULL OR deleted_at_utc >= created_at_utc)
        );

        INSERT INTO fixed_region_profile_directory(profile_id, name, created_at_utc, updated_at_utc, deleted_at_utc)
        SELECT profile_id, 'Profile ' || substr(profile_id, 1, 72), MIN(created_at_utc), MAX(created_at_utc), NULL
        FROM recurring_fixed_region_profile_versions
        GROUP BY profile_id;

        CREATE INDEX idx_fixed_region_profile_directory_live_id
            ON fixed_region_profile_directory(profile_id)
            WHERE deleted_at_utc IS NULL;

        CREATE TABLE fixed_region_profile_create_idempotency (
            idempotency_key TEXT NOT NULL PRIMARY KEY COLLATE BINARY CHECK (length(idempotency_key) BETWEEN 1 AND 128 AND length(trim(idempotency_key, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND instr(idempotency_key, char(47)) = 0 AND instr(idempotency_key, char(92)) = 0),
            method TEXT NOT NULL CHECK (method = 'POST'),
            request_path TEXT NOT NULL CHECK (request_path = '/api/v1/profiles'),
            request_hash TEXT NOT NULL COLLATE BINARY CHECK (length(request_hash) = 64 AND request_hash NOT GLOB '*[^0-9a-f]*'),
            profile_id TEXT NOT NULL COLLATE BINARY,
            result_version INTEGER NOT NULL CHECK (result_version = 1),
            result_digest TEXT NOT NULL COLLATE BINARY CHECK (length(result_digest) = length('recurring-fixed-region-profile/v1:') + 64),
            result_name TEXT NOT NULL CHECK (length(trim(result_name, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) BETWEEN 1 AND 80 AND length(result_name) <= 160),
            result_etag TEXT NOT NULL CHECK (length(result_etag) BETWEEN 3 AND 128),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0)
        );

        CREATE TRIGGER trg_fixed_region_profile_directory_insert
            AFTER INSERT ON recurring_fixed_region_profile_versions
            WHEN NOT EXISTS (SELECT 1 FROM fixed_region_profile_directory WHERE profile_id = NEW.profile_id)
            BEGIN
                INSERT INTO fixed_region_profile_directory(profile_id, name, created_at_utc, updated_at_utc, deleted_at_utc)
                VALUES (NEW.profile_id, 'Profile ' || substr(NEW.profile_id, 1, 72), NEW.created_at_utc, NEW.created_at_utc, NULL);
            END;

        CREATE TRIGGER trg_recurring_plan_profile_bindings_reject_deleted_profile
            BEFORE INSERT ON recurring_plan_profile_bindings
            WHEN EXISTS (SELECT 1 FROM fixed_region_profile_directory WHERE profile_id = NEW.profile_id AND deleted_at_utc IS NOT NULL)
            BEGIN
                SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_reject_deleted_profile');
            END;

        CREATE TRIGGER trg_fixed_region_profile_create_idempotency_immutable_update
            BEFORE UPDATE ON fixed_region_profile_create_idempotency
            BEGIN
                SELECT RAISE(ABORT, 'fixed_region_profile_create_idempotency_is_immutable');
            END;

        CREATE TRIGGER trg_fixed_region_profile_create_idempotency_immutable_delete
            BEFORE DELETE ON fixed_region_profile_create_idempotency
            BEGIN
                SELECT RAISE(ABORT, 'fixed_region_profile_create_idempotency_is_immutable');
            END;
        """;

    private static SqliteMigrationDefinition CreateMigration()
    {
        var canonical = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonical);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
            throw new InvalidOperationException($"The fixed schema v21 definition checksum is {checksum}.");
        return new SqliteMigrationDefinition(21, MigrationName, canonical, checksum);
    }

    private static SqliteCheckConstraintDefinition Check(string table, string pattern) =>
        new(table, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation) =>
        new(name, "fixed_region_profile_create_idempotency", Array.Empty<Regex>(), new Regex(
            $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{operation}\\s+ON\\s+fixed_region_profile_create_idempotency.*fixed_region_profile_create_idempotency_is_immutable.*END\\s*;?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));
}
