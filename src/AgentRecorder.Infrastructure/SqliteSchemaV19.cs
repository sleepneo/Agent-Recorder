using System.Text.RegularExpressions;

namespace AgentRecorder.Infrastructure;

/// <summary>V19 stores one-shot future-window grants independently of fixed-region Lease data.</summary>
internal static class SqliteSchemaV19
{
    public const string MigrationName = "schema_v19_future_window_one_shot";
    public const string MigrationChecksum = "d06ea5108551af24ec3ce12cfde986f37b7d4ccbd3a3eaf446ffc0d36b02ad76";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(19, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("future_window_authorizations", new[]
        {
            new SqliteColumnDefinition("authorization_id", "TEXT", true, 1),
            new SqliteColumnDefinition("idempotency_key", "TEXT", true, 0),
            new SqliteColumnDefinition("request_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("executable_path", "TEXT", true, 0),
            new SqliteColumnDefinition("executable_file_identity", "TEXT", true, 0),
            new SqliteColumnDefinition("executable_sha256", "TEXT", true, 0),
            new SqliteColumnDefinition("signer_subject", "TEXT", false, 0),
            new SqliteColumnDefinition("signer_certificate_sha256", "TEXT", false, 0),
            new SqliteColumnDefinition("system_audio_endpoint_id", "TEXT", false, 0),
            new SqliteColumnDefinition("system_audio_endpoint_name", "TEXT", false, 0),
            new SqliteColumnDefinition("maximum_duration_seconds", "INTEGER", true, 0),
            new SqliteColumnDefinition("validity_seconds", "INTEGER", true, 0),
            new SqliteColumnDefinition("output_directory", "TEXT", true, 0),
            new SqliteColumnDefinition("output_file_name", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("approved_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("expires_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("approval_id", "TEXT", false, 0),
            new SqliteColumnDefinition("run_id", "TEXT", false, 0),
            new SqliteColumnDefinition("window_id", "TEXT", false, 0),
            new SqliteColumnDefinition("process_id", "INTEGER", false, 0),
            new SqliteColumnDefinition("process_creation_filetime_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("run_status", "TEXT", false, 0),
            new SqliteColumnDefinition("proof_id", "TEXT", false, 0),
            new SqliteColumnDefinition("proof_nonce", "TEXT", false, 0),
            new SqliteColumnDefinition("output_path", "TEXT", false, 0),
            new SqliteColumnDefinition("output_size_bytes", "INTEGER", false, 0),
            new SqliteColumnDefinition("actual_duration_ms", "INTEGER", false, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_future_window_authorizations_status", "future_window_authorizations", false,
            new[] { "status_code", "updated_at_utc" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("future_window_authorizations", new[] { "current_user_sid", "session_binding", "idempotency_key" }),
        new SqliteUniqueConstraintDefinition("future_window_authorizations", new[] { "authorization_id" }),
        new SqliteUniqueConstraintDefinition("future_window_authorizations", new[] { "approval_id" }),
        new SqliteUniqueConstraintDefinition("future_window_authorizations", new[] { "run_id" }),
        new SqliteUniqueConstraintDefinition("future_window_authorizations", new[] { "proof_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();
    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("request_digest\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(request_digest\\)\\s*=\\s*64"),
        Check("executable_sha256\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(executable_sha256\\)\\s*=\\s*64"),
        Check("status_code\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(status_code\\s+IN"),
        Check("maximum_duration_seconds\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(maximum_duration_seconds\\s+BETWEEN\\s+1\\s+AND\\s+600"),
        Check("validity_seconds\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(validity_seconds\\s+BETWEEN\\s+1\\s+AND\\s+3600"),
        Check("proof_nonce\\s+TEXT\\s+NULL.*CHECK\\s*\\(proof_nonce\\s+IS\\s+NULL"),
        Check("CHECK\\s*\\(\\s*\\(status_code\\s*=\\s*'pending'.*status_code\\s*=\\s*'completed'.*\\)"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTriggerDefinition("trg_future_window_authorization_transition", "future_window_authorizations", new[]
        {
            new Regex("BEFORE\\s+UPDATE\\s+ON\\s+future_window_authorizations", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("future_window_authorization_immutable_binding", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("future_window_authorization_invalid_transition", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            new Regex("future_window_authorization_version_invalid", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        }),
        ImmutableTrigger("trg_future_window_authorization_no_delete", "DELETE"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE future_window_authorizations (
            authorization_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(authorization_id)) > 0),
            idempotency_key TEXT NOT NULL CHECK (length(trim(idempotency_key)) BETWEEN 1 AND 128 AND idempotency_key = trim(idempotency_key)),
            request_digest TEXT NOT NULL CHECK (length(request_digest) = 64 AND request_digest = lower(request_digest) AND request_digest NOT GLOB '*[^0-9a-f]*'),
            current_user_sid TEXT NOT NULL CHECK (length(trim(current_user_sid)) > 0),
            session_binding TEXT NOT NULL CHECK (length(trim(session_binding)) > 0),
            executable_path TEXT NOT NULL CHECK (length(trim(executable_path)) > 0 AND executable_path = trim(executable_path)),
            executable_file_identity TEXT NOT NULL CHECK (length(trim(executable_file_identity)) > 0),
            executable_sha256 TEXT NOT NULL CHECK (length(executable_sha256) = 64 AND executable_sha256 = lower(executable_sha256) AND executable_sha256 NOT GLOB '*[^0-9a-f]*'),
            signer_subject TEXT NULL CHECK (signer_subject IS NULL OR length(trim(signer_subject)) > 0),
            signer_certificate_sha256 TEXT NULL CHECK (signer_certificate_sha256 IS NULL OR (length(signer_certificate_sha256) = 64 AND signer_certificate_sha256 = lower(signer_certificate_sha256) AND signer_certificate_sha256 NOT GLOB '*[^0-9a-f]*')),
            system_audio_endpoint_id TEXT NULL CHECK (system_audio_endpoint_id IS NULL OR length(trim(system_audio_endpoint_id)) > 0),
            system_audio_endpoint_name TEXT NULL CHECK (system_audio_endpoint_name IS NULL OR length(trim(system_audio_endpoint_name)) > 0),
            maximum_duration_seconds INTEGER NOT NULL CHECK (maximum_duration_seconds BETWEEN 1 AND 600),
            validity_seconds INTEGER NOT NULL CHECK (validity_seconds BETWEEN 1 AND 3600),
            output_directory TEXT NOT NULL CHECK (length(trim(output_directory)) > 0 AND output_directory = trim(output_directory)),
            output_file_name TEXT NOT NULL CHECK (length(trim(output_file_name)) BETWEEN 1 AND 180 AND output_file_name = trim(output_file_name) AND instr(output_file_name, '/') = 0 AND instr(output_file_name, char(92)) = 0),
            status_code TEXT NOT NULL CHECK (status_code IN ('pending', 'active', 'used', 'revoked', 'expired', 'blocked', 'failed', 'completed')),
            reason_code TEXT NULL CHECK (reason_code IS NULL OR (length(trim(reason_code)) > 0 AND reason_code = trim(reason_code))),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc > 0),
            approved_at_utc INTEGER NULL CHECK (approved_at_utc IS NULL OR approved_at_utc > 0),
            expires_at_utc INTEGER NULL CHECK (expires_at_utc IS NULL OR expires_at_utc > 0),
            approval_id TEXT NULL UNIQUE CHECK (approval_id IS NULL OR length(trim(approval_id)) > 0),
            run_id TEXT NULL UNIQUE CHECK (run_id IS NULL OR length(trim(run_id)) > 0),
            window_id TEXT NULL CHECK (window_id IS NULL OR length(trim(window_id)) > 0),
            process_id INTEGER NULL CHECK (process_id IS NULL OR process_id > 0),
            process_creation_filetime_utc INTEGER NULL CHECK (process_creation_filetime_utc IS NULL OR process_creation_filetime_utc > 0),
            run_status TEXT NULL CHECK (run_status IS NULL OR run_status IN ('start_committed', 'recording', 'finalizing', 'completed', 'stopped', 'failed', 'started_unknown')),
            proof_id TEXT NULL UNIQUE CHECK (proof_id IS NULL OR length(trim(proof_id)) > 0),
            proof_nonce TEXT NULL CHECK (proof_nonce IS NULL OR (length(proof_nonce) = 32 AND proof_nonce = lower(proof_nonce) AND proof_nonce NOT GLOB '*[^0-9a-f]*')),
            output_path TEXT NULL CHECK (output_path IS NULL OR (length(trim(output_path)) > 0 AND output_path = trim(output_path))),
            output_size_bytes INTEGER NULL CHECK (output_size_bytes IS NULL OR output_size_bytes > 0),
            actual_duration_ms INTEGER NULL CHECK (actual_duration_ms IS NULL OR actual_duration_ms >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            UNIQUE (current_user_sid, session_binding, idempotency_key),
            CHECK ((system_audio_endpoint_id IS NULL AND system_audio_endpoint_name IS NULL) OR (system_audio_endpoint_id IS NOT NULL AND system_audio_endpoint_name IS NOT NULL)),
            CHECK ((status_code = 'pending' AND approved_at_utc IS NULL AND expires_at_utc IS NULL AND approval_id IS NULL AND run_id IS NULL AND window_id IS NULL AND process_id IS NULL AND process_creation_filetime_utc IS NULL AND run_status IS NULL AND proof_id IS NULL AND proof_nonce IS NULL) OR
                   (status_code = 'active' AND approved_at_utc IS NOT NULL AND expires_at_utc > approved_at_utc AND approval_id IS NOT NULL AND run_id IS NULL AND window_id IS NULL AND process_id IS NULL AND process_creation_filetime_utc IS NULL AND run_status IS NULL AND proof_id IS NULL AND proof_nonce IS NULL) OR
                   (status_code IN ('used', 'completed') AND approved_at_utc IS NOT NULL AND expires_at_utc > approved_at_utc AND approval_id IS NOT NULL AND run_id IS NOT NULL AND window_id IS NOT NULL AND process_id IS NOT NULL AND process_creation_filetime_utc IS NOT NULL AND run_status IS NOT NULL AND proof_id IS NOT NULL AND proof_nonce IS NOT NULL) OR
                   status_code IN ('revoked', 'expired', 'blocked', 'failed')),
            CHECK ((run_status IS NULL AND run_id IS NULL AND proof_id IS NULL AND proof_nonce IS NULL) OR
                   (run_status IS NOT NULL AND run_id IS NOT NULL AND window_id IS NOT NULL AND process_id IS NOT NULL AND process_creation_filetime_utc IS NOT NULL AND proof_id IS NOT NULL AND proof_nonce IS NOT NULL)),
            CHECK ((output_path IS NULL AND output_size_bytes IS NULL AND actual_duration_ms IS NULL) OR
                   (status_code = 'completed' AND run_status = 'completed' AND output_path IS NOT NULL AND output_size_bytes IS NOT NULL AND actual_duration_ms IS NOT NULL))
        );

        CREATE INDEX idx_future_window_authorizations_status
            ON future_window_authorizations(status_code, updated_at_utc);

        CREATE TRIGGER trg_future_window_authorization_transition
            BEFORE UPDATE ON future_window_authorizations
            BEGIN
                SELECT RAISE(ABORT, 'future_window_authorization_immutable_binding')
                WHERE NEW.authorization_id != OLD.authorization_id OR
                      NEW.idempotency_key != OLD.idempotency_key OR NEW.request_digest != OLD.request_digest OR
                      NEW.current_user_sid != OLD.current_user_sid OR NEW.session_binding != OLD.session_binding OR
                      NEW.executable_path != OLD.executable_path OR NEW.executable_file_identity != OLD.executable_file_identity OR
                      NEW.executable_sha256 != OLD.executable_sha256 OR NEW.signer_subject IS NOT OLD.signer_subject OR
                      NEW.signer_certificate_sha256 IS NOT OLD.signer_certificate_sha256 OR
                      NEW.system_audio_endpoint_id IS NOT OLD.system_audio_endpoint_id OR
                      NEW.system_audio_endpoint_name IS NOT OLD.system_audio_endpoint_name OR
                      NEW.maximum_duration_seconds != OLD.maximum_duration_seconds OR NEW.validity_seconds != OLD.validity_seconds OR
                      NEW.output_directory != OLD.output_directory OR NEW.output_file_name != OLD.output_file_name OR
                      NEW.created_at_utc != OLD.created_at_utc;
                SELECT RAISE(ABORT, 'future_window_authorization_invalid_transition')
                WHERE NOT (
                    (OLD.status_code = 'pending' AND NEW.status_code IN ('active', 'revoked', 'expired', 'blocked', 'failed')) OR
                    (OLD.status_code = 'active' AND NEW.status_code IN ('used', 'revoked', 'expired', 'blocked', 'failed')) OR
                    (OLD.status_code = 'used' AND NEW.status_code IN ('revoked', 'failed', 'completed')) OR
                    (OLD.status_code = 'used' AND NEW.status_code = 'used' AND OLD.run_status = 'start_committed' AND NEW.run_status = 'started_unknown') OR
                    (OLD.status_code = 'revoked' AND NEW.status_code = 'revoked')
                );
                SELECT RAISE(ABORT, 'future_window_authorization_version_invalid')
                WHERE NEW.version != OLD.version + 1 OR NEW.updated_at_utc < OLD.updated_at_utc;
            END;

        CREATE TRIGGER trg_future_window_authorization_no_delete
            BEFORE DELETE ON future_window_authorizations
            BEGIN SELECT RAISE(ABORT, 'future_window_authorization_is_immutable'); END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("future_window_authorizations", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation) =>
        new(name, "future_window_authorizations", Array.Empty<Regex>(), new Regex(
            $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{Regex.Escape(operation)}\\s+ON\\s+future_window_authorizations\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'future_window_authorization_is_immutable'\\s*\\)\\s*;\\s*END\\s*;?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
            throw new InvalidOperationException($"The fixed schema v19 definition checksum changed: {checksum}.");
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}
