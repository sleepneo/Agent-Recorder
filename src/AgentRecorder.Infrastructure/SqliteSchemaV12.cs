using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

internal static class SqliteSchemaV12
{
    public const string MigrationName = "schema_v12_recurring_lease_local_approvals";
    public const string MigrationChecksum = "5c2e13129873818b6b89f9005031850ed5daf172b41d2ec9a7c8f5e80913835b";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(12, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recurring_lease_local_approvals", new[]
        {
            new SqliteColumnDefinition("approval_id", "TEXT", true, 1),
            new SqliteColumnDefinition("lease_id", "TEXT", true, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("configuration_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("authorization_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("approved_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("approval_kind_code", "TEXT", true, 0),
            new SqliteColumnDefinition("approval_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("approval_digest", "TEXT", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition(
            "ux_recurring_consent_leases_exact_approval_parent",
            "recurring_consent_leases",
            true,
            new[] { "lease_id", "plan_id", "configuration_digest", "authorization_digest" }),
        new SqliteIndexDefinition(
            "ux_recurring_consent_leases_one_active_per_plan",
            "recurring_consent_leases",
            true,
            new[] { "plan_id" },
            PredicatePattern: new Regex("WHERE\\s+status_code\\s*=\\s*'active'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            DefinitionPattern: new Regex("CREATE\\s+UNIQUE\\s+INDEX\\s+ux_recurring_consent_leases_one_active_per_plan\\s+ON\\s+recurring_consent_leases\\s*\\(\\s*plan_id\\s*\\)\\s+WHERE\\s+status_code\\s*=\\s*'active'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("recurring_lease_local_approvals", new[] { "lease_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("recurring_lease_local_approvals", new[]
        {
            new SqliteForeignKeyInfo(
                "recurring_consent_leases", "NO ACTION", "RESTRICT",
                new[] { "lease_id", "plan_id", "configuration_digest", "authorization_digest" },
                new[] { "lease_id", "plan_id", "configuration_digest", "authorization_digest" }),
        }, RequireCompatibleCollation: true),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("approval_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(approval_id"),
        Check("lease_id\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(trim\\(lease_id"),
        Check("configuration_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-plan-configuration/v1:"),
        Check("authorization_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-consent-lease-authorization/v1:"),
        Check("current_user_sid\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(current_user_sid\\)\\s+BETWEEN\\s+1\\s+AND\\s+256"),
        Check("session_binding\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*length\\(session_binding\\)\\s+BETWEEN\\s+1\\s+AND\\s+256"),
        Check("approved_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(approved_at_utc\\s*>=\\s*0\\)"),
        Check("approval_kind_code\\s+TEXT\\s+NOT NULL.*approval_kind_code\\s*=\\s*'recurring_lease_local_approval'"),
        Check("approval_version\\s+INTEGER\\s+NOT NULL.*approval_version\\s*=\\s*1"),
        Check("approval_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*recurring-lease-local-approval/v1:"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        ImmutableTrigger("trg_recurring_lease_local_approvals_immutable_update", "UPDATE", "recurring_lease_local_approvals", "recurring_lease_local_approvals_are_immutable"),
        ImmutableTrigger("trg_recurring_lease_local_approvals_immutable_delete", "DELETE", "recurring_lease_local_approvals", "recurring_lease_local_approvals_are_immutable"),
    });

    private const string SchemaDefinition = """
        CREATE UNIQUE INDEX ux_recurring_consent_leases_exact_approval_parent
            ON recurring_consent_leases(lease_id, plan_id, configuration_digest, authorization_digest);

        CREATE UNIQUE INDEX ux_recurring_consent_leases_one_active_per_plan
            ON recurring_consent_leases(plan_id)
            WHERE status_code = 'active';

        CREATE TABLE recurring_lease_local_approvals (
            approval_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(approval_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND approval_id = trim(approval_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            lease_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND lease_id = trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            plan_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND plan_id = trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            configuration_digest TEXT NOT NULL COLLATE BINARY CHECK (length(configuration_digest) = length('recurring-plan-configuration/v1:') + 64 AND substr(configuration_digest, 1, length('recurring-plan-configuration/v1:')) = 'recurring-plan-configuration/v1:' AND substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1) = lower(substr(configuration_digest, length('recurring-plan-configuration/v1:') + 1))),
            authorization_digest TEXT NOT NULL COLLATE BINARY CHECK (length(authorization_digest) = length('recurring-consent-lease-authorization/v1:') + 64 AND substr(authorization_digest, 1, length('recurring-consent-lease-authorization/v1:')) = 'recurring-consent-lease-authorization/v1:' AND substr(authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1) = lower(substr(authorization_digest, length('recurring-consent-lease-authorization/v1:') + 1))),
            current_user_sid TEXT NOT NULL COLLATE BINARY CHECK (length(current_user_sid) BETWEEN 1 AND 256 AND current_user_sid = trim(current_user_sid, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            session_binding TEXT NOT NULL COLLATE BINARY CHECK (length(session_binding) BETWEEN 1 AND 256 AND session_binding = trim(session_binding, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))),
            approved_at_utc INTEGER NOT NULL CHECK (approved_at_utc >= 0),
            approval_kind_code TEXT NOT NULL COLLATE BINARY CHECK (approval_kind_code = 'recurring_lease_local_approval'),
            approval_version INTEGER NOT NULL CHECK (approval_version = 1),
            approval_digest TEXT NOT NULL COLLATE BINARY CHECK (length(approval_digest) = length('recurring-lease-local-approval/v1:') + 64 AND substr(approval_digest, 1, length('recurring-lease-local-approval/v1:')) = 'recurring-lease-local-approval/v1:' AND substr(approval_digest, length('recurring-lease-local-approval/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(approval_digest, length('recurring-lease-local-approval/v1:') + 1) = lower(substr(approval_digest, length('recurring-lease-local-approval/v1:') + 1))),
            PRIMARY KEY (approval_id),
            UNIQUE (lease_id),
            FOREIGN KEY (lease_id, plan_id, configuration_digest, authorization_digest)
                REFERENCES recurring_consent_leases(lease_id, plan_id, configuration_digest, authorization_digest)
                ON DELETE RESTRICT
        );

        CREATE TRIGGER trg_recurring_lease_local_approvals_immutable_update
            BEFORE UPDATE ON recurring_lease_local_approvals
            BEGIN
                SELECT RAISE(ABORT, 'recurring_lease_local_approvals_are_immutable');
            END;

        CREATE TRIGGER trg_recurring_lease_local_approvals_immutable_delete
            BEFORE DELETE ON recurring_lease_local_approvals
            BEGIN
                SELECT RAISE(ABORT, 'recurring_lease_local_approvals_are_immutable');
            END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("recurring_lease_local_approvals", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation, string tableName, string message) =>
        new(
            name,
            tableName,
            Array.Empty<Regex>(),
            new Regex(
                $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{operation}\\s+ON\\s+{Regex.Escape(tableName)}\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'{Regex.Escape(message)}'\\s*\\)\\s*;\\s*END\\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixed schema v12 definition checksum changed.");
        }

        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
