using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// V15 stores the one recurring setup preparation relation. It is deliberately
/// append-only: V1-V14 definitions and checksums are historical protocol.
/// </summary>
internal static class SqliteSchemaV15
{
    public const string MigrationName = "schema_v15_recurring_setup_preparations";

    public const string MigrationChecksum = "40d1f0ca5654842294a42482a35846a794622ffc0e7f20caf496a332949f7650";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(15, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recurring_setup_preparations", new[]
        {
            new SqliteColumnDefinition("intent_id", "TEXT", true, 1),
            new SqliteColumnDefinition("selection_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("selected_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("profile_id", "TEXT", true, 0),
            new SqliteColumnDefinition("profile_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("profile_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("lease_id", "TEXT", true, 0),
            new SqliteColumnDefinition("configuration_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("prepared_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("preparation_version", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition(
            "idx_recurring_setup_preparations_plan_id",
            "recurring_setup_preparations",
            false,
            new[] { "plan_id" }),
        new SqliteIndexDefinition(
            "idx_recurring_setup_preparations_lease_id",
            "recurring_setup_preparations",
            false,
            new[] { "lease_id" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("recurring_setup_preparations", new[] { "plan_id" }),
        new SqliteUniqueConstraintDefinition("recurring_setup_preparations", new[] { "lease_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("recurring_setup_preparations", new[]
        {
            new SqliteForeignKeyInfo("setup_intents", "NO ACTION", "RESTRICT", new[] { "intent_id" }, new[] { "intent_id" }),
            new SqliteForeignKeyInfo("plans", "NO ACTION", "RESTRICT", new[] { "plan_id" }, new[] { "id" }),
            new SqliteForeignKeyInfo(
                "recurring_fixed_region_profile_versions",
                "NO ACTION",
                "RESTRICT",
                new[] { "profile_id", "profile_version", "profile_digest" },
                new[] { "profile_id", "profile_version", "profile_digest" }),
            new SqliteForeignKeyInfo(
                "recurring_consent_leases",
                "NO ACTION",
                "RESTRICT",
                new[] { "lease_id", "plan_id" },
                new[] { "lease_id", "plan_id" }),
        }, RequireCompatibleCollation: true),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("intent_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(intent_id"),
        Check("selection_digest\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(selection_digest\\)\\s*=\\s*length\\('recurring-fixed-region-selection/v1:'\\)\\s*\\+\\s*64"),
        Check("selected_at_utc\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(selected_at_utc\\s*>=\\s*0\\)"),
        Check("plan_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(plan_id"),
        Check("profile_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(profile_id"),
        Check("profile_version\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(profile_version\\s*=\\s*1\\)"),
        Check("profile_digest\\s+TEXT\\s+NOT NULL.*recurring-fixed-region-profile/v1:"),
        Check("lease_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(lease_id"),
        Check("configuration_digest\\s+TEXT\\s+NOT NULL.*recurring-plan-configuration/v1:"),
        Check("prepared_at_utc\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(prepared_at_utc\\s*>=\\s*0\\)"),
        Check("preparation_version\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(preparation_version\\s*=\\s*1\\)"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        ImmutableTrigger("trg_recurring_setup_preparations_immutable_update", "UPDATE"),
        ImmutableTrigger("trg_recurring_setup_preparations_immutable_delete", "DELETE"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE recurring_setup_preparations (
            intent_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(intent_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND length(intent_id) <= 128 AND instr(intent_id, char(47)) = 0 AND instr(intent_id, char(92)) = 0),
            selection_digest TEXT NOT NULL COLLATE BINARY CHECK (length(selection_digest) = length('recurring-fixed-region-selection/v1:') + 64 AND substr(selection_digest, 1, length('recurring-fixed-region-selection/v1:')) = 'recurring-fixed-region-selection/v1:' AND substr(selection_digest, length('recurring-fixed-region-selection/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(selection_digest, length('recurring-fixed-region-selection/v1:') + 1) = lower(substr(selection_digest, length('recurring-fixed-region-selection/v1:') + 1))),
            selected_at_utc INTEGER NOT NULL CHECK (selected_at_utc >= 0),
            plan_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND length(plan_id) <= 128 AND instr(plan_id, char(47)) = 0 AND instr(plan_id, char(92)) = 0),
            profile_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(profile_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND length(profile_id) <= 128 AND instr(profile_id, char(47)) = 0 AND instr(profile_id, char(92)) = 0 AND instr(profile_id, char(58)) = 0),
            profile_version INTEGER NOT NULL CHECK (profile_version = 1),
            profile_digest TEXT NOT NULL COLLATE BINARY CHECK (length(profile_digest) = length('recurring-fixed-region-profile/v1:') + 64 AND substr(profile_digest, 1, length('recurring-fixed-region-profile/v1:')) = 'recurring-fixed-region-profile/v1:' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) = lower(substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1))),
            lease_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND length(lease_id) <= 128 AND instr(lease_id, char(47)) = 0 AND instr(lease_id, char(92)) = 0),
            configuration_digest TEXT NOT NULL COLLATE BINARY CHECK (length(configuration_digest) = length('recurring-plan-configuration/v1:') + 64 AND substr(configuration_digest, 1, length('recurring-plan-configuration/v1:')) = 'recurring-plan-configuration/v1:' AND substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1) = lower(substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1))),
            prepared_at_utc INTEGER NOT NULL CHECK (prepared_at_utc >= 0),
            preparation_version INTEGER NOT NULL CHECK (preparation_version = 1),
            PRIMARY KEY (intent_id),
            UNIQUE (plan_id),
            UNIQUE (lease_id),
            FOREIGN KEY (intent_id) REFERENCES setup_intents(intent_id) ON DELETE RESTRICT,
            FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT,
            FOREIGN KEY (profile_id, profile_version, profile_digest) REFERENCES recurring_fixed_region_profile_versions(profile_id, profile_version, profile_digest) ON DELETE RESTRICT,
            FOREIGN KEY (lease_id, plan_id) REFERENCES recurring_consent_leases(lease_id, plan_id) ON DELETE RESTRICT
        );

        CREATE INDEX idx_recurring_setup_preparations_plan_id
            ON recurring_setup_preparations(plan_id);

        CREATE INDEX idx_recurring_setup_preparations_lease_id
            ON recurring_setup_preparations(lease_id);

        CREATE TRIGGER trg_recurring_setup_preparations_immutable_update
            BEFORE UPDATE ON recurring_setup_preparations
            BEGIN
                SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable');
            END;

        CREATE TRIGGER trg_recurring_setup_preparations_immutable_delete
            BEFORE DELETE ON recurring_setup_preparations
            BEGIN
                SELECT RAISE(ABORT, 'recurring_setup_preparations_are_immutable');
            END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("recurring_setup_preparations", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation) =>
        new(
            name,
            "recurring_setup_preparations",
            Array.Empty<Regex>(),
            new Regex(
                $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{Regex.Escape(operation)}\\s+ON\\s+recurring_setup_preparations\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_setup_preparations_are_immutable'\\s*\\)\\s*;\\s*END\\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixed schema v15 definition checksum changed.");
        }

        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
