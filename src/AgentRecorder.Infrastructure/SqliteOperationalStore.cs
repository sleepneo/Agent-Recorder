using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Infrastructure;

/// <summary>
/// Owns the Phase 3 operational SQLite file, connection policy, and fixed schema migrations.
/// This is intentionally a storage seam only; it does not create repositories or domain objects.
/// </summary>
public sealed class SqliteOperationalStore
{
    public const int CurrentSchemaVersion = 2;
    public const int BusyTimeoutMilliseconds = 5_000;
    public const string DatabaseFileName = "agent-recorder.db";
    public const string StateDirectoryName = "state";

    public SqliteOperationalStore(string? databasePath = null)
    {
        DatabasePath = Path.GetFullPath(string.IsNullOrWhiteSpace(databasePath)
            ? GetDefaultDatabasePath()
            : databasePath);

        if (string.IsNullOrWhiteSpace(Path.GetFileName(DatabasePath)))
        {
            throw new ArgumentException("The SQLite database path must name a file.", nameof(databasePath));
        }
    }

    public string DatabasePath { get; }

    public static string GetDefaultDatabasePath() =>
        Path.Combine(DataDirResolver.Resolve(), StateDirectoryName, DatabaseFileName);

    /// <summary>
    /// Opens a business connection with all connection PRAGMAs applied centrally.
    /// The connection is not a global singleton and callers own its lifetime.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        EnsureParentDirectory();
        SqliteConnection? connection = null;
        try
        {
            connection = CreateConnection();
            ConfigureBusinessConnection(connection);
            return connection;
        }
        catch (SqliteOperationalStoreException)
        {
            connection?.Dispose();
            throw;
        }
        catch (SqliteException exception)
        {
            connection?.Dispose();
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "The SQLite operational store could not be opened or validated.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            connection?.Dispose();
            throw new SqliteOperationalStoreException(
                "sqlite_open_failed",
                "The SQLite operational store could not be opened.",
                exception);
        }
    }

    /// <summary>
    /// Applies every fixed migration exactly once and validates migration history.
    /// </summary>
    public void Initialize()
    {
        using var connection = OpenInitializationConnection();
        SqliteTransaction? transaction = null;

        try
        {
            transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);
            var migrations = SqliteSchemaCatalog.Migrations;
            var hasMigrationTable = TableExists(connection, transaction, "schema_migrations");
            var applied = hasMigrationTable
                ? ReadAppliedMigrations(connection, transaction)
                : new Dictionary<int, AppliedMigration>();

            ValidateMigrationHistory(applied, migrations);

            foreach (var migration in migrations)
            {
                if (applied.TryGetValue(migration.Version, out _))
                {
                    continue;
                }

                try
                {
                    ExecuteFixedDefinition(connection, transaction, migration.Definition);
                    InsertMigrationRecord(connection, transaction, migration);
                }
                catch (SqliteOperationalStoreException)
                {
                    throw;
                }
                catch (SqliteException exception)
                {
                    throw new SqliteOperationalStoreException(
                        "sqlite_migration_failed",
                        $"SQLite migration {migration.Version} ({migration.Name}) failed.",
                        exception);
                }
            }

            ValidateSchemaShape(connection, transaction);
            transaction.Commit();
        }
        catch (SqliteOperationalStoreException)
        {
            TryRollback(transaction);
            throw;
        }
        catch (SqliteException exception)
        {
            TryRollback(transaction);
            throw new SqliteOperationalStoreException(
                "sqlite_migration_failed",
                "SQLite migration processing failed.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            TryRollback(transaction);
            throw new SqliteOperationalStoreException(
                "sqlite_migration_failed",
                "SQLite migration processing failed.",
                exception);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    private SqliteConnection OpenInitializationConnection()
    {
        SqliteConnection? connection = null;
        try
        {
            EnsureParentDirectory();
            connection = CreateConnection();
            ConfigureInitializationConnection(connection);
            return connection;
        }
        catch (SqliteOperationalStoreException)
        {
            connection?.Dispose();
            throw;
        }
        catch (SqliteException exception)
        {
            connection?.Dispose();
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "The SQLite operational store could not be opened or validated.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            connection?.Dispose();
            throw new SqliteOperationalStoreException(
                "sqlite_open_failed",
                "The SQLite operational store could not be opened.",
                exception);
        }
    }

    private SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void ConfigureInitializationConnection(SqliteConnection connection)
    {
        ApplyConnectionPolicy(connection);

        var integrity = ExecuteScalarString(connection, "PRAGMA integrity_check;");
        if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "SQLite integrity_check did not return ok.");
        }

        var journalMode = ExecuteScalarString(connection, "PRAGMA journal_mode = WAL;");
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteOperationalStoreException(
                "sqlite_journal_mode_failed",
                "SQLite did not accept WAL journal mode.");
        }

        ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");

        if (ExecuteScalarInt64(connection, "PRAGMA foreign_keys;") != 1 ||
            ExecuteScalarInt64(connection, "PRAGMA busy_timeout;") != BusyTimeoutMilliseconds ||
            ExecuteScalarInt64(connection, "PRAGMA synchronous;") != 1 ||
            !string.Equals(ExecuteScalarString(connection, "PRAGMA journal_mode;"), "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteOperationalStoreException(
                "sqlite_pragma_policy_failed",
                "SQLite connection PRAGMA policy was not applied.");
        }
    }

    private static void ConfigureBusinessConnection(SqliteConnection connection)
    {
        ApplyConnectionPolicy(connection);
        EnsureInitializedForBusinessConnection(connection);

        if (!string.Equals(ExecuteScalarString(connection, "PRAGMA journal_mode;"), "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteOperationalStoreException(
                "sqlite_journal_mode_mismatch",
                "The initialized SQLite operational store is not using WAL journal mode.");
        }
    }

    private static void ApplyConnectionPolicy(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON;");
        ExecuteNonQuery(connection, $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
        ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL;");

        if (ExecuteScalarInt64(connection, "PRAGMA foreign_keys;") != 1 ||
            ExecuteScalarInt64(connection, "PRAGMA busy_timeout;") != BusyTimeoutMilliseconds ||
            ExecuteScalarInt64(connection, "PRAGMA synchronous;") != 1)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_pragma_policy_failed",
                "SQLite connection PRAGMA policy was not applied.");
        }
    }

    private static void EnsureInitializedForBusinessConnection(SqliteConnection connection)
    {
        try
        {
            if (!TableExists(connection, transaction: null, "schema_migrations"))
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_not_initialized",
                    "The SQLite operational store has not been initialized.");
            }

            var highestVersion = ExecuteScalarInt64(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;");
            if (highestVersion > SqliteOperationalStore.CurrentSchemaVersion)
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_future_schema",
                    $"SQLite schema version {highestVersion} is newer than supported version {SqliteOperationalStore.CurrentSchemaVersion}.");
            }

            if (highestVersion < SqliteOperationalStore.CurrentSchemaVersion)
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_not_initialized",
                    "The SQLite operational store has not applied the current schema.");
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT version, name, definition_checksum
                FROM schema_migrations
                WHERE version = $version
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$version", SqliteOperationalStore.CurrentSchemaVersion);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_not_initialized",
                    "The SQLite operational store has not applied the current schema.");
            }

            var version = reader.GetInt32(0);
            var name = reader.GetString(1);
            var checksum = reader.GetString(2);
            var migration = SqliteSchemaCatalog.Migrations.SingleOrDefault(item => item.Version == version);
            if (version > SqliteOperationalStore.CurrentSchemaVersion)
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_future_schema",
                    $"SQLite schema version {version} is newer than supported version {SqliteOperationalStore.CurrentSchemaVersion}.");
            }

            if (migration is null ||
                !string.Equals(name, migration.Name, StringComparison.Ordinal) ||
                !string.Equals(checksum, migration.Checksum, StringComparison.Ordinal))
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_migration_checksum_mismatch",
                    $"SQLite migration version {version} does not match the compiled migration definition.");
            }
        }
        catch (SqliteOperationalStoreException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "The initialized SQLite schema marker could not be read.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "The initialized SQLite schema marker contains invalid values.",
                exception);
        }
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static Dictionary<int, AppliedMigration> ReadAppliedMigrations(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var applied = new Dictionary<int, AppliedMigration>();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT version, name, definition_checksum, applied_at_utc
                FROM schema_migrations
                ORDER BY version;
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var version = reader.GetInt32(0);
                var migration = new AppliedMigration(
                    version,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3));
                if (migration.AppliedAtUtc < 0)
                {
                    throw new SqliteOperationalStoreException(
                        "sqlite_corrupt",
                        $"schema_migrations contains an invalid applied time for version {version}.");
                }

                if (!applied.TryAdd(version, migration))
                {
                    throw new SqliteOperationalStoreException(
                        "sqlite_corrupt",
                        $"schema_migrations contains duplicate version {version}.");
                }
            }

            return applied;
        }
        catch (SqliteOperationalStoreException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "schema_migrations could not be read.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "schema_migrations contains invalid values.",
                exception);
        }
    }

    private static void ValidateMigrationHistory(
        IReadOnlyDictionary<int, AppliedMigration> applied,
        IReadOnlyList<SqliteMigrationDefinition> migrations)
    {
        var supported = migrations.ToDictionary(migration => migration.Version);
        foreach (var record in applied.Values)
        {
            if (record.Version > CurrentSchemaVersion)
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_future_schema",
                    $"SQLite schema version {record.Version} is newer than supported version {CurrentSchemaVersion}.");
            }

            if (!supported.TryGetValue(record.Version, out var migration) ||
                !string.Equals(record.Name, migration.Name, StringComparison.Ordinal) ||
                !string.Equals(record.DefinitionChecksum, migration.Checksum, StringComparison.Ordinal))
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_migration_checksum_mismatch",
                    $"SQLite migration version {record.Version} does not match the compiled migration definition.");
            }
        }
    }

    private static void ExecuteFixedDefinition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string definition)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = definition;
        command.ExecuteNonQuery();
    }

    private static void InsertMigrationRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteMigrationDefinition migration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations(version, name, definition_checksum, applied_at_utc)
            VALUES ($version, $name, $checksum, $appliedAtUtc);
            """;
        command.Parameters.AddWithValue("$version", migration.Version);
        command.Parameters.AddWithValue("$name", migration.Name);
        command.Parameters.AddWithValue("$checksum", migration.Checksum);
        command.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.Ticks);
        command.ExecuteNonQuery();
    }

    private static void ValidateSchemaShape(SqliteConnection connection, SqliteTransaction transaction)
    {
        try
        {
            ValidateSchemaShapeCore(connection, transaction);
        }
        catch (SqliteOperationalStoreException)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "SQLite schema metadata could not be validated.",
                exception);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or IndexOutOfRangeException)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "SQLite schema metadata contains invalid values.",
                exception);
        }
    }

    private static void ValidateSchemaShapeCore(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var table in SqliteSchemaCatalog.Tables)
        {
            if (!TableExists(connection, transaction, table.Name))
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_corrupt",
                    $"SQLite schema is missing required table {table.Name}.");
            }

            ValidateColumns(connection, transaction, table);
        }

        foreach (var index in SqliteSchemaCatalog.Indexes)
        {
            ValidateExplicitIndex(connection, transaction, index);
        }

        foreach (var unique in SqliteSchemaCatalog.UniqueConstraints)
        {
            ValidateUniqueConstraint(connection, transaction, unique);
        }

        foreach (var foreignKey in SqliteSchemaCatalog.ForeignKeys)
        {
            ValidateForeignKeys(connection, transaction, foreignKey);
        }

        foreach (var deferredForeignKey in SqliteSchemaCatalog.DeferredForeignKeys)
        {
            ValidateDeferredForeignKey(connection, transaction, deferredForeignKey);
        }

        ValidateForeignKeyCheck(connection, transaction);

        foreach (var checkConstraint in SqliteSchemaCatalog.CheckConstraints)
        {
            ValidateCheckConstraint(connection, transaction, checkConstraint);
        }
    }

    private static void ValidateColumns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteTableDefinition expected)
    {
        var actual = new List<SqliteColumnDefinition>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(expected.Name)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            actual.Add(new SqliteColumnDefinition(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                reader.GetInt32(5)));
        }

        if (actual.Count != expected.Columns.Count)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite schema table {expected.Name} has unexpected columns.");
        }

        for (var index = 0; index < expected.Columns.Count; index++)
        {
            var expectedColumn = expected.Columns[index];
            var actualColumn = actual[index];
            if (!string.Equals(actualColumn.Name, expectedColumn.Name, StringComparison.Ordinal) ||
                !string.Equals(actualColumn.DeclaredType, expectedColumn.DeclaredType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(GetAffinity(actualColumn.DeclaredType), expectedColumn.Affinity, StringComparison.Ordinal) ||
                actualColumn.NotNull != expectedColumn.NotNull ||
                actualColumn.PrimaryKeyPosition != expectedColumn.PrimaryKeyPosition)
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_corrupt",
                    $"SQLite schema column {expected.Name}.{expectedColumn.Name} does not match the compiled definition.");
            }
        }
    }

    private static void ValidateExplicitIndex(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteIndexDefinition expected)
    {
        var actualIndexes = GetIndexes(connection, transaction, expected.TableName);
        var actual = actualIndexes.SingleOrDefault(index => string.Equals(index.Name, expected.Name, StringComparison.Ordinal));
        if (actual is null || actual.Unique != expected.Unique || actual.Partial)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite schema index {expected.Name} does not match the compiled definition.");
        }

        using (var ownerCommand = connection.CreateCommand())
        {
            ownerCommand.Transaction = transaction;
            ownerCommand.CommandText = "SELECT tbl_name FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
            ownerCommand.Parameters.AddWithValue("$name", expected.Name);
            if (!string.Equals(ownerCommand.ExecuteScalar() as string, expected.TableName, StringComparison.Ordinal))
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_corrupt",
                    $"SQLite schema index {expected.Name} belongs to the wrong table.");
            }
        }

        if (!GetIndexColumns(connection, transaction, expected.Name).SequenceEqual(expected.Columns, StringComparer.Ordinal))
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite schema index {expected.Name} has unexpected columns.");
        }
    }

    private static void ValidateUniqueConstraint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteUniqueConstraintDefinition expected)
    {
        var found = GetIndexes(connection, transaction, expected.TableName)
            .Where(index => index.Unique && !index.Partial)
            .Select(index => GetIndexColumns(connection, transaction, index.Name))
            .Any(columns => columns.SequenceEqual(expected.Columns, StringComparer.Ordinal));
        if (!found)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite schema is missing UNIQUE ({string.Join(", ", expected.Columns)}) on {expected.TableName}.");
        }
    }

    private static void ValidateForeignKeys(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteForeignKeyDefinition expected)
    {
        var actual = GetForeignKeys(connection, transaction, expected.TableName)
            .OrderBy(foreignKey => foreignKey.TargetTable, StringComparer.Ordinal)
            .ThenBy(foreignKey => string.Join("\u001f", foreignKey.FromColumns), StringComparer.Ordinal)
            .ToArray();
        var expectedForeignKeys = expected.ForeignKeys
            .OrderBy(foreignKey => foreignKey.TargetTable, StringComparer.Ordinal)
            .ThenBy(foreignKey => string.Join("\u001f", foreignKey.FromColumns), StringComparer.Ordinal)
            .ToArray();

        if (actual.Length != expectedForeignKeys.Length ||
            actual.Where((foreignKey, index) => !foreignKey.Matches(expectedForeignKeys[index])).Any())
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite foreign keys for {expected.TableName} do not match the compiled definition.");
        }
    }

    private static void ValidateDeferredForeignKey(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteDeferredForeignKeyDefinition expected)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", expected.TableName);
        var sql = command.ExecuteScalar() as string;
        var normalized = Regex.Replace(sql ?? string.Empty, @"\s+", " ").Trim();
        if (!expected.Pattern.IsMatch(normalized))
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite deferred foreign key on {expected.TableName} does not match the compiled definition.");
        }
    }

    private static void ValidateCheckConstraint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteCheckConstraintDefinition expected)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", expected.TableName);
        var sql = command.ExecuteScalar() as string;
        var normalized = Regex.Replace(sql ?? string.Empty, @"\s+", " ").Trim();
        if (!expected.Pattern.IsMatch(normalized))
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite CHECK constraints for {expected.TableName} do not match the compiled definition.");
        }
    }

    private static void ValidateForeignKeyCheck(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                "SQLite foreign_key_check reported a constraint violation.");
        }
    }

    private static List<SqliteIndexInfo> GetIndexes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA index_list({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var result = new List<SqliteIndexInfo>();
        while (reader.Read())
        {
            result.Add(new SqliteIndexInfo(
                reader.GetString(1),
                reader.GetInt32(2) == 1,
                reader.FieldCount > 4 && reader.GetInt32(4) == 1));
        }

        return result;
    }

    private static IReadOnlyList<string> GetIndexColumns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT seqno, name FROM pragma_index_info($indexName) ORDER BY seqno;";
        command.Parameters.AddWithValue("$indexName", indexName);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
        }

        return result;
    }

    private static List<SqliteForeignKeyInfo> GetForeignKeys(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA foreign_key_list({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var grouped = new Dictionary<int, SqliteForeignKeyInfoBuilder>();
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            if (!grouped.TryGetValue(id, out var builder))
            {
                builder = new SqliteForeignKeyInfoBuilder(
                    reader.GetString(2),
                    reader.GetString(5),
                    reader.GetString(6));
                grouped.Add(id, builder);
            }

            builder.Add(reader.GetInt32(1), reader.GetString(3), reader.GetString(4));
        }

        return grouped.Values.Select(builder => builder.Build()).ToList();
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string GetAffinity(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal))
        {
            return "INTEGER";
        }

        if (type.Contains("CHAR", StringComparison.Ordinal) ||
            type.Contains("CLOB", StringComparison.Ordinal) ||
            type.Contains("TEXT", StringComparison.Ordinal))
        {
            return "TEXT";
        }

        if (type.Contains("BLOB", StringComparison.Ordinal) || type.Length == 0)
        {
            return "BLOB";
        }

        if (type.Contains("REAL", StringComparison.Ordinal) ||
            type.Contains("FLOA", StringComparison.Ordinal) ||
            type.Contains("DOUB", StringComparison.Ordinal))
        {
            return "REAL";
        }

        return "NUMERIC";
    }

    private void EnsureParentDirectory()
    {
        var parent = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new SqliteOperationalStoreException(
                "sqlite_path_invalid",
                "The SQLite database path has no parent directory.");
        }

        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_path_unavailable",
                "The SQLite database directory could not be created.",
                exception);
        }
    }

    private static void TryRollback(SqliteTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            transaction.Rollback();
        }
        catch (InvalidOperationException)
        {
            // The provider has already rolled back or disposed the transaction.
        }
        catch (SqliteException)
        {
            // Preserve the original stable migration error.
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ExecuteScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long ExecuteScalarInt64(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private sealed record AppliedMigration(int Version, string Name, string DefinitionChecksum, long AppliedAtUtc);
}

internal sealed record SqliteColumnDefinition(
    string Name,
    string DeclaredType,
    bool NotNull,
    int PrimaryKeyPosition)
{
    public string Affinity => DeclaredType.ToUpperInvariant() switch
    {
        "INTEGER" => "INTEGER",
        "TEXT" => "TEXT",
        _ => throw new InvalidOperationException($"Unsupported fixed SQLite type {DeclaredType}."),
    };
}

internal sealed record SqliteTableDefinition(string Name, IReadOnlyList<SqliteColumnDefinition> Columns);

internal sealed record SqliteIndexDefinition(
    string Name,
    string TableName,
    bool Unique,
    IReadOnlyList<string> Columns);

internal sealed record SqliteUniqueConstraintDefinition(
    string TableName,
    IReadOnlyList<string> Columns);

internal sealed record SqliteForeignKeyDefinition(
    string TableName,
    IReadOnlyList<SqliteForeignKeyInfo> ForeignKeys);

internal sealed record SqliteDeferredForeignKeyDefinition(string TableName, Regex Pattern);

internal sealed record SqliteCheckConstraintDefinition(string TableName, Regex Pattern);

internal sealed record SqliteIndexInfo(string Name, bool Unique, bool Partial);

internal sealed class SqliteForeignKeyInfoBuilder
{
    private readonly List<(int Seq, string From, string To)> columns = new();

    public SqliteForeignKeyInfoBuilder(string targetTable, string onUpdate, string onDelete)
    {
        TargetTable = targetTable;
        OnUpdate = onUpdate;
        OnDelete = onDelete;
    }

    public string TargetTable { get; }
    public string OnUpdate { get; }
    public string OnDelete { get; }

    public void Add(int seq, string from, string to) => columns.Add((seq, from, to));

    public SqliteForeignKeyInfo Build() => new(
        TargetTable,
        OnUpdate,
        OnDelete,
        columns.OrderBy(column => column.Seq).Select(column => column.From).ToArray(),
        columns.OrderBy(column => column.Seq).Select(column => column.To).ToArray());
}

internal sealed record SqliteForeignKeyInfo(
    string TargetTable,
    string OnUpdate,
    string OnDelete,
    IReadOnlyList<string> FromColumns,
    IReadOnlyList<string> ToColumns)
{
    public bool Matches(SqliteForeignKeyInfo expected) =>
        string.Equals(TargetTable, expected.TargetTable, StringComparison.Ordinal) &&
        string.Equals(OnUpdate, expected.OnUpdate, StringComparison.Ordinal) &&
        string.Equals(OnDelete, expected.OnDelete, StringComparison.Ordinal) &&
        FromColumns.SequenceEqual(expected.FromColumns, StringComparer.Ordinal) &&
        ToColumns.SequenceEqual(expected.ToColumns, StringComparer.Ordinal);
}

internal sealed record SqliteMigrationDefinition(int Version, string Name, string Definition, string Checksum);

internal static class SqliteSchemaV1
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } =
        Array.AsReadOnly(new[]
        {
            CreateMigration(1, "schema_v1_operational_domain"),
        });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("schema_migrations", new[]
        {
            new SqliteColumnDefinition("version", "INTEGER", true, 1),
            new SqliteColumnDefinition("name", "TEXT", true, 0),
            new SqliteColumnDefinition("definition_checksum", "TEXT", true, 0),
            new SqliteColumnDefinition("applied_at_utc", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("plans", new[]
        {
            new SqliteColumnDefinition("id", "TEXT", true, 1),
            new SqliteColumnDefinition("is_one_time", "INTEGER", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("plan_occurrences", new[]
        {
            new SqliteColumnDefinition("id", "TEXT", true, 1),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("window_start_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("window_end_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("run_id", "TEXT", false, 0),
            new SqliteColumnDefinition("terminal_reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("recording_runs", new[]
        {
            new SqliteColumnDefinition("id", "TEXT", true, 1),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("has_crossed_start_commit", "INTEGER", true, 0),
            new SqliteColumnDefinition("media_artifact_id", "TEXT", false, 0),
            new SqliteColumnDefinition("bundle_id", "TEXT", false, 0),
            new SqliteColumnDefinition("terminal_reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("consent_leases", new[]
        {
            new SqliteColumnDefinition("id", "TEXT", true, 1),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("valid_from_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("valid_until_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("max_uses", "INTEGER", true, 0),
            new SqliteColumnDefinition("max_duration_ms", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("lease_uses", new[]
        {
            new SqliteColumnDefinition("id", "TEXT", true, 1),
            new SqliteColumnDefinition("lease_id", "TEXT", true, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("run_id", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("reserved_use_count", "INTEGER", true, 0),
            new SqliteColumnDefinition("reserved_duration_ms", "INTEGER", true, 0),
            new SqliteColumnDefinition("actual_settled_duration_ms", "INTEGER", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_plan_occurrences_plan_window", "plan_occurrences", false, new[] { "plan_id", "window_start_utc", "window_end_utc" }),
        new SqliteIndexDefinition("idx_plan_occurrences_status_window", "plan_occurrences", false, new[] { "status_code", "window_start_utc" }),
        new SqliteIndexDefinition("idx_recording_runs_status_updated", "recording_runs", false, new[] { "status_code", "updated_at_utc" }),
        new SqliteIndexDefinition("idx_consent_leases_plan_status", "consent_leases", false, new[] { "plan_id", "status_code" }),
        new SqliteIndexDefinition("idx_consent_leases_occurrence_status", "consent_leases", false, new[] { "occurrence_id", "status_code" }),
        new SqliteIndexDefinition("idx_lease_uses_lease_status", "lease_uses", false, new[] { "lease_id", "status_code" }),
        new SqliteIndexDefinition("idx_lease_uses_occurrence_status", "lease_uses", false, new[] { "occurrence_id", "status_code" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("schema_migrations", new[] { "name" }),
        new SqliteUniqueConstraintDefinition("plan_occurrences", new[] { "id", "plan_id" }),
        new SqliteUniqueConstraintDefinition("plan_occurrences", new[] { "run_id" }),
        new SqliteUniqueConstraintDefinition("recording_runs", new[] { "id", "occurrence_id" }),
        new SqliteUniqueConstraintDefinition("recording_runs", new[] { "occurrence_id" }),
        new SqliteUniqueConstraintDefinition("consent_leases", new[] { "occurrence_id" }),
        new SqliteUniqueConstraintDefinition("consent_leases", new[] { "id", "occurrence_id" }),
        new SqliteUniqueConstraintDefinition("lease_uses", new[] { "run_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("plan_occurrences", new[]
        {
            new SqliteForeignKeyInfo("plans", "NO ACTION", "RESTRICT", new[] { "plan_id" }, new[] { "id" }),
            new SqliteForeignKeyInfo("recording_runs", "NO ACTION", "RESTRICT", new[] { "run_id", "id" }, new[] { "id", "occurrence_id" }),
        }),
        new SqliteForeignKeyDefinition("recording_runs", new[]
        {
            new SqliteForeignKeyInfo("plan_occurrences", "NO ACTION", "RESTRICT", new[] { "occurrence_id" }, new[] { "id" }),
        }),
        new SqliteForeignKeyDefinition("consent_leases", new[]
        {
            new SqliteForeignKeyInfo("plans", "NO ACTION", "RESTRICT", new[] { "plan_id" }, new[] { "id" }),
            new SqliteForeignKeyInfo("plan_occurrences", "NO ACTION", "RESTRICT", new[] { "occurrence_id", "plan_id" }, new[] { "id", "plan_id" }),
        }),
        new SqliteForeignKeyDefinition("lease_uses", new[]
        {
            new SqliteForeignKeyInfo("consent_leases", "NO ACTION", "RESTRICT", new[] { "lease_id", "occurrence_id" }, new[] { "id", "occurrence_id" }),
            new SqliteForeignKeyInfo("recording_runs", "NO ACTION", "RESTRICT", new[] { "run_id", "occurrence_id" }, new[] { "id", "occurrence_id" }),
        }),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteDeferredForeignKeyDefinition(
            "plan_occurrences",
            new Regex(@"FOREIGN KEY\s*\(run_id,\s*id\)\s*REFERENCES\s*recording_runs\s*\(id,\s*occurrence_id\)\s*ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
        new SqliteDeferredForeignKeyDefinition(
            "recording_runs",
            new Regex(@"FOREIGN KEY\s*\(occurrence_id\)\s*REFERENCES\s*plan_occurrences\s*\(id\)\s*ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
    });

    private const string SchemaDefinition = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER NOT NULL PRIMARY KEY CHECK (version > 0),
            name TEXT NOT NULL UNIQUE CHECK (length(trim(name, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            definition_checksum TEXT NOT NULL CHECK (length(definition_checksum) = 64),
            applied_at_utc INTEGER NOT NULL CHECK (applied_at_utc >= 0)
        );

        CREATE TABLE plans (
            id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            is_one_time INTEGER NOT NULL CHECK (is_one_time IN (0, 1)),
            status_code TEXT NOT NULL CHECK (length(trim(status_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= 0 AND updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0)
        );

        CREATE TABLE plan_occurrences (
            id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            plan_id TEXT NOT NULL CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            status_code TEXT NOT NULL CHECK (length(trim(status_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            window_start_utc INTEGER NOT NULL CHECK (window_start_utc >= 0),
            window_end_utc INTEGER NOT NULL CHECK (window_end_utc >= 0 AND window_start_utc < window_end_utc),
            run_id TEXT NULL CHECK (run_id IS NULL OR length(trim(run_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            terminal_reason_code TEXT NULL CHECK (terminal_reason_code IS NULL OR length(trim(terminal_reason_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= 0 AND updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            UNIQUE (id, plan_id),
            UNIQUE (run_id),
            FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT,
            FOREIGN KEY (run_id, id) REFERENCES recording_runs(id, occurrence_id)
                ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED
        );

        CREATE TABLE recording_runs (
            id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            occurrence_id TEXT NOT NULL CHECK (length(trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            status_code TEXT NOT NULL CHECK (length(trim(status_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            has_crossed_start_commit INTEGER NOT NULL CHECK (has_crossed_start_commit IN (0, 1)),
            media_artifact_id TEXT NULL CHECK (media_artifact_id IS NULL OR length(trim(media_artifact_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            bundle_id TEXT NULL CHECK (bundle_id IS NULL OR length(trim(bundle_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            terminal_reason_code TEXT NULL CHECK (terminal_reason_code IS NULL OR length(trim(terminal_reason_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= 0 AND updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            UNIQUE (id, occurrence_id),
            UNIQUE (occurrence_id),
            FOREIGN KEY (occurrence_id) REFERENCES plan_occurrences(id) ON DELETE RESTRICT
                DEFERRABLE INITIALLY DEFERRED
        );

        CREATE TABLE consent_leases (
            id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            plan_id TEXT NOT NULL CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            occurrence_id TEXT NOT NULL UNIQUE CHECK (length(trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            status_code TEXT NOT NULL CHECK (length(trim(status_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            valid_from_utc INTEGER NOT NULL CHECK (valid_from_utc >= 0),
            valid_until_utc INTEGER NOT NULL CHECK (valid_until_utc >= 0 AND valid_from_utc < valid_until_utc),
            max_uses INTEGER NOT NULL CHECK (max_uses > 0),
            max_duration_ms INTEGER NOT NULL CHECK (max_duration_ms > 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= 0 AND updated_at_utc >= valid_from_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            UNIQUE (id, occurrence_id),
            FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT,
            FOREIGN KEY (occurrence_id, plan_id) REFERENCES plan_occurrences(id, plan_id)
                ON DELETE RESTRICT
        );

        CREATE TABLE lease_uses (
            id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            lease_id TEXT NOT NULL CHECK (length(trim(lease_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            occurrence_id TEXT NOT NULL CHECK (length(trim(occurrence_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            run_id TEXT NOT NULL UNIQUE CHECK (length(trim(run_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            status_code TEXT NOT NULL CHECK (length(trim(status_code, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            reserved_use_count INTEGER NOT NULL CHECK (reserved_use_count >= 0),
            reserved_duration_ms INTEGER NOT NULL CHECK (reserved_duration_ms >= 0),
            actual_settled_duration_ms INTEGER NULL CHECK (actual_settled_duration_ms >= 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= 0 AND updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            FOREIGN KEY (lease_id, occurrence_id) REFERENCES consent_leases(id, occurrence_id)
                ON DELETE RESTRICT,
            FOREIGN KEY (run_id, occurrence_id) REFERENCES recording_runs(id, occurrence_id)
                ON DELETE RESTRICT
        );

        CREATE INDEX idx_plan_occurrences_plan_window
            ON plan_occurrences(plan_id, window_start_utc, window_end_utc);
        CREATE INDEX idx_plan_occurrences_status_window
            ON plan_occurrences(status_code, window_start_utc);
        CREATE INDEX idx_recording_runs_status_updated
            ON recording_runs(status_code, updated_at_utc);
        CREATE INDEX idx_consent_leases_plan_status
            ON consent_leases(plan_id, status_code);
        CREATE INDEX idx_consent_leases_occurrence_status
            ON consent_leases(occurrence_id, status_code);
        CREATE INDEX idx_lease_uses_lease_status
            ON lease_uses(lease_id, status_code);
        CREATE INDEX idx_lease_uses_occurrence_status
            ON lease_uses(occurrence_id, status_code);
        """;

    internal static string CanonicalizeDefinition(string definition) =>
        definition.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    internal static string ComputeChecksum(string definition)
    {
        var canonical = CanonicalizeDefinition(definition);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = CanonicalizeDefinition(SchemaDefinition);
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, ComputeChecksum(canonicalDefinition));
    }
}

internal static class SqliteSchemaV2
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(2, "schema_v2_authorized_capture_scopes"),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("authorized_capture_scopes", new[]
        {
            new SqliteColumnDefinition("scope_id", "TEXT", true, 1),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", true, 0),
            new SqliteColumnDefinition("lease_id", "TEXT", true, 0),
            new SqliteColumnDefinition("authorization_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("scope_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("target_type_code", "TEXT", true, 0),
            new SqliteColumnDefinition("capture_semantics_code", "TEXT", true, 0),
            new SqliteColumnDefinition("coordinate_space_code", "TEXT", true, 0),
            new SqliteColumnDefinition("display_identity_status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("stable_display_fingerprint", "TEXT", true, 0),
            new SqliteColumnDefinition("display_bounds_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("display_bounds_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("region_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("dpi_x", "INTEGER", true, 0),
            new SqliteColumnDefinition("dpi_y", "INTEGER", true, 0),
            new SqliteColumnDefinition("physical_width", "INTEGER", true, 0),
            new SqliteColumnDefinition("physical_height", "INTEGER", true, 0),
            new SqliteColumnDefinition("orientation_code", "TEXT", true, 0),
            new SqliteColumnDefinition("backend_code", "TEXT", true, 0),
            new SqliteColumnDefinition("audio_mode_code", "TEXT", true, 0),
            new SqliteColumnDefinition("reserved_duration_ms", "INTEGER", true, 0),
            new SqliteColumnDefinition("countdown_seconds", "INTEGER", true, 0),
            new SqliteColumnDefinition("output_directory", "TEXT", true, 0),
            new SqliteColumnDefinition("frozen_file_name", "TEXT", true, 0),
            new SqliteColumnDefinition("output_conflict_policy_code", "TEXT", true, 0),
            new SqliteColumnDefinition("wake_policy_code", "TEXT", true, 0),
            new SqliteColumnDefinition("desktop_requirement_code", "TEXT", true, 0),
            new SqliteColumnDefinition("current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("topology_digest", "TEXT", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_authorized_capture_scopes_plan", "authorized_capture_scopes", false, new[] { "plan_id" }),
        new SqliteIndexDefinition("idx_authorized_capture_scopes_occurrence", "authorized_capture_scopes", false, new[] { "occurrence_id" }),
        new SqliteIndexDefinition("idx_authorized_capture_scopes_lease", "authorized_capture_scopes", false, new[] { "lease_id" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("authorized_capture_scopes", new[] { "lease_id" }),
        new SqliteUniqueConstraintDefinition("authorized_capture_scopes", new[] { "plan_id", "occurrence_id" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("authorized_capture_scopes", new[]
        {
            new SqliteForeignKeyInfo("plans", "NO ACTION", "RESTRICT", new[] { "plan_id" }, new[] { "id" }),
            new SqliteForeignKeyInfo("plan_occurrences", "NO ACTION", "RESTRICT", new[] { "occurrence_id", "plan_id" }, new[] { "id", "plan_id" }),
            new SqliteForeignKeyInfo("consent_leases", "NO ACTION", "RESTRICT", new[] { "lease_id", "occurrence_id" }, new[] { "id", "occurrence_id" }),
        }),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check(@"scope_id\s+TEXT\s+NOT NULL\s+PRIMARY KEY\s+CHECK\s*\(length\(trim\(scope_id\)\)\s*>\s*0\)"),
        Check(@"plan_id\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(plan_id\)\)\s*>\s*0\)"),
        Check(@"occurrence_id\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(occurrence_id\)\)\s*>\s*0\)"),
        Check(@"lease_id\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(lease_id\)\)\s*>\s*0\)"),
        Check(@"authorization_version\s+INTEGER\s+NOT NULL\s+CHECK\s*\(authorization_version\s*=\s*1\)"),
        Check(@"created_at_utc\s+INTEGER\s+NOT NULL\s+CHECK\s*\(created_at_utc\s*>=\s*0\)"),
        Check(@"scope_digest\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(scope_digest\)\s*=\s*64\s+AND\s+scope_digest\s*=\s*lower\(scope_digest\)\s+AND\s+scope_digest\s+NOT GLOB\s+'\*\[\^0-9a-f\]\*'\)"),
        Check(@"target_type_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(target_type_code\s*=\s*'fixed_region'\)"),
        Check(@"capture_semantics_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(capture_semantics_code\s*=\s*'desktop_region'\)"),
        Check(@"coordinate_space_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(coordinate_space_code\s*=\s*'physical_virtual_screen'\)"),
        Check(@"display_identity_status_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(display_identity_status_code\s*=\s*'resolved'\)"),
        Check(@"stable_display_fingerprint\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(stable_display_fingerprint\)\)\s*>\s*0\)"),
        Check(@"display_bounds_width\s+INTEGER\s+NOT NULL\s+CHECK\s*\(display_bounds_width\s*>\s*0\)"),
        Check(@"display_bounds_height\s+INTEGER\s+NOT NULL\s+CHECK\s*\(display_bounds_height\s*>\s*0\)"),
        Check(@"region_x\s+INTEGER\s+NOT NULL\s+CHECK\s*\(region_x\s*>=\s*0\)"),
        Check(@"region_y\s+INTEGER\s+NOT NULL\s+CHECK\s*\(region_y\s*>=\s*0\)"),
        Check(@"region_width\s+INTEGER\s+NOT NULL\s+CHECK\s*\(region_width\s*>\s*0\s+AND\s+region_x\s*\+\s*region_width\s*<=\s*display_bounds_width\)"),
        Check(@"region_height\s+INTEGER\s+NOT NULL\s+CHECK\s*\(region_height\s*>\s*0\s+AND\s+region_y\s*\+\s*region_height\s*<=\s*display_bounds_height\)"),
        Check(@"dpi_x\s+INTEGER\s+NOT NULL\s+CHECK\s*\(dpi_x\s*>\s*0\)"),
        Check(@"dpi_y\s+INTEGER\s+NOT NULL\s+CHECK\s*\(dpi_y\s*>\s*0\)"),
        Check(@"physical_width\s+INTEGER\s+NOT NULL\s+CHECK\s*\(physical_width\s*>\s*0\s+AND\s+physical_width\s*=\s*display_bounds_width\)"),
        Check(@"physical_height\s+INTEGER\s+NOT NULL\s+CHECK\s*\(physical_height\s*>\s*0\s+AND\s+physical_height\s*=\s*display_bounds_height\)"),
        Check(@"orientation_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(orientation_code\s+IN\s*\('landscape',\s*'portrait',\s*'landscape_flipped',\s*'portrait_flipped'\)\)"),
        Check(@"backend_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(backend_code\s*=\s*'ffmpeg-region'\)"),
        Check(@"audio_mode_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(audio_mode_code\s*=\s*'none'\)"),
        Check(@"reserved_duration_ms\s+INTEGER\s+NOT NULL\s+CHECK\s*\(reserved_duration_ms\s*>\s*0\s+AND\s+reserved_duration_ms\s*<=\s*600000\)"),
        Check(@"countdown_seconds\s+INTEGER\s+NOT NULL\s+CHECK\s*\(countdown_seconds\s*>=\s*0\)"),
        Check(@"output_directory\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(output_directory\)\)\s*>\s*0\)"),
        Check(@"frozen_file_name\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(frozen_file_name\)\)\s*>\s*0\s+AND\s+instr\(frozen_file_name,\s*char\(47\)\)\s*=\s*0\s+AND\s+instr\(frozen_file_name,\s*char\(92\)\)\s*=\s*0\)"),
        Check(@"output_conflict_policy_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(output_conflict_policy_code\s*=\s*'fail_if_exists'\)"),
        Check(@"wake_policy_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(wake_policy_code\s*=\s*'natural_wake_only'\)"),
        Check(@"desktop_requirement_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(desktop_requirement_code\s*=\s*'interactive_desktop_required'\)"),
        Check(@"current_user_sid\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(current_user_sid\)\)\s*>\s*0\)"),
        Check(@"session_binding\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(session_binding\)\)\s*>\s*0\)"),
        Check(@"topology_digest\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(topology_digest\)\s*=\s*64\s+AND\s+topology_digest\s*=\s*lower\(topology_digest\)\s+AND\s+topology_digest\s+NOT GLOB\s+'\*\[\^0-9a-f\]\*'\)"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE authorized_capture_scopes (
            scope_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(scope_id)) > 0),
            plan_id TEXT NOT NULL CHECK (length(trim(plan_id)) > 0),
            occurrence_id TEXT NOT NULL CHECK (length(trim(occurrence_id)) > 0),
            lease_id TEXT NOT NULL CHECK (length(trim(lease_id)) > 0),
            authorization_version INTEGER NOT NULL CHECK (authorization_version = 1),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            scope_digest TEXT NOT NULL CHECK (length(scope_digest) = 64 AND scope_digest = lower(scope_digest) AND scope_digest NOT GLOB '*[^0-9a-f]*'),
            target_type_code TEXT NOT NULL CHECK (target_type_code = 'fixed_region'),
            capture_semantics_code TEXT NOT NULL CHECK (capture_semantics_code = 'desktop_region'),
            coordinate_space_code TEXT NOT NULL CHECK (coordinate_space_code = 'physical_virtual_screen'),
            display_identity_status_code TEXT NOT NULL CHECK (display_identity_status_code = 'resolved'),
            stable_display_fingerprint TEXT NOT NULL CHECK (length(trim(stable_display_fingerprint)) > 0),
            display_bounds_x INTEGER NOT NULL,
            display_bounds_y INTEGER NOT NULL,
            display_bounds_width INTEGER NOT NULL CHECK (display_bounds_width > 0),
            display_bounds_height INTEGER NOT NULL CHECK (display_bounds_height > 0),
            region_x INTEGER NOT NULL CHECK (region_x >= 0),
            region_y INTEGER NOT NULL CHECK (region_y >= 0),
            region_width INTEGER NOT NULL CHECK (region_width > 0 AND region_x + region_width <= display_bounds_width),
            region_height INTEGER NOT NULL CHECK (region_height > 0 AND region_y + region_height <= display_bounds_height),
            dpi_x INTEGER NOT NULL CHECK (dpi_x > 0),
            dpi_y INTEGER NOT NULL CHECK (dpi_y > 0),
            physical_width INTEGER NOT NULL CHECK (physical_width > 0 AND physical_width = display_bounds_width),
            physical_height INTEGER NOT NULL CHECK (physical_height > 0 AND physical_height = display_bounds_height),
            orientation_code TEXT NOT NULL CHECK (orientation_code IN ('landscape', 'portrait', 'landscape_flipped', 'portrait_flipped')),
            backend_code TEXT NOT NULL CHECK (backend_code = 'ffmpeg-region'),
            audio_mode_code TEXT NOT NULL CHECK (audio_mode_code = 'none'),
            reserved_duration_ms INTEGER NOT NULL CHECK (reserved_duration_ms > 0 AND reserved_duration_ms <= 600000),
            countdown_seconds INTEGER NOT NULL CHECK (countdown_seconds >= 0),
            output_directory TEXT NOT NULL CHECK (length(trim(output_directory)) > 0),
            frozen_file_name TEXT NOT NULL CHECK (length(trim(frozen_file_name)) > 0 AND instr(frozen_file_name, char(47)) = 0 AND instr(frozen_file_name, char(92)) = 0),
            output_conflict_policy_code TEXT NOT NULL CHECK (output_conflict_policy_code = 'fail_if_exists'),
            wake_policy_code TEXT NOT NULL CHECK (wake_policy_code = 'natural_wake_only'),
            desktop_requirement_code TEXT NOT NULL CHECK (desktop_requirement_code = 'interactive_desktop_required'),
            current_user_sid TEXT NOT NULL CHECK (length(trim(current_user_sid)) > 0),
            session_binding TEXT NOT NULL CHECK (length(trim(session_binding)) > 0),
            topology_digest TEXT NOT NULL CHECK (length(topology_digest) = 64 AND topology_digest = lower(topology_digest) AND topology_digest NOT GLOB '*[^0-9a-f]*'),
            UNIQUE (lease_id),
            UNIQUE (plan_id, occurrence_id),
            FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT,
            FOREIGN KEY (occurrence_id, plan_id) REFERENCES plan_occurrences(id, plan_id) ON DELETE RESTRICT,
            FOREIGN KEY (lease_id, occurrence_id) REFERENCES consent_leases(id, occurrence_id) ON DELETE RESTRICT
        );

        CREATE INDEX idx_authorized_capture_scopes_plan
            ON authorized_capture_scopes(plan_id);
        CREATE INDEX idx_authorized_capture_scopes_occurrence
            ON authorized_capture_scopes(occurrence_id);
        CREATE INDEX idx_authorized_capture_scopes_lease
            ON authorized_capture_scopes(lease_id);
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("authorized_capture_scopes", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, SqliteSchemaV1.ComputeChecksum(canonicalDefinition));
    }
}

internal static class SqliteSchemaCatalog
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } =
        SqliteSchemaV1.Migrations.Concat(SqliteSchemaV2.Migrations).ToArray();

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } =
        SqliteSchemaV1.Tables.Concat(SqliteSchemaV2.Tables).ToArray();

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } =
        SqliteSchemaV1.Indexes.Concat(SqliteSchemaV2.Indexes).ToArray();

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } =
        SqliteSchemaV1.UniqueConstraints.Concat(SqliteSchemaV2.UniqueConstraints).ToArray();

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } =
        SqliteSchemaV1.ForeignKeys.Concat(SqliteSchemaV2.ForeignKeys).ToArray();

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } =
        SqliteSchemaV1.DeferredForeignKeys.Concat(SqliteSchemaV2.DeferredForeignKeys).ToArray();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints => SqliteSchemaV2.CheckConstraints;
}
