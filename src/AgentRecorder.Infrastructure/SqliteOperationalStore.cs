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
    public const int CurrentSchemaVersion = 13;
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

        foreach (var trigger in SqliteSchemaCatalog.Triggers)
        {
            ValidateTrigger(connection, transaction, trigger);
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
        if (actual is null || actual.Unique != expected.Unique || actual.Partial != (expected.PredicatePattern is not null))
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

        if (expected.PredicatePattern is not null)
        {
            using var predicateCommand = connection.CreateCommand();
            predicateCommand.Transaction = transaction;
            predicateCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
            predicateCommand.Parameters.AddWithValue("$name", expected.Name);
            var sql = predicateCommand.ExecuteScalar() as string;
            var normalized = Regex.Replace(sql ?? string.Empty, @"\s+", " ").Trim();
            if (!expected.PredicatePattern.IsMatch(normalized))
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_corrupt",
                    $"SQLite schema index {expected.Name} has an unexpected predicate.");
            }
        }

        if (expected.DefinitionPattern is not null)
        {
            using var definitionCommand = connection.CreateCommand();
            definitionCommand.Transaction = transaction;
            definitionCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
            definitionCommand.Parameters.AddWithValue("$name", expected.Name);
            var sql = definitionCommand.ExecuteScalar() as string;
            var normalized = Regex.Replace(sql ?? string.Empty, @"\s+", " ").Trim();
            if (!expected.DefinitionPattern.IsMatch(normalized))
            {
                throw new SqliteOperationalStoreException(
                    "sqlite_corrupt",
                    $"SQLite schema index {expected.Name} has an unexpected definition.");
            }
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

        if (expected.RequireCompatibleCollation)
        {
            foreach (var foreignKey in expectedForeignKeys)
            {
                ValidateForeignKeyCollation(connection, transaction, expected.TableName, foreignKey);
            }
        }
    }

    private static void ValidateForeignKeyCollation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string childTable,
        SqliteForeignKeyInfo foreignKey)
    {
        var parentIndex = GetIndexes(connection, transaction, foreignKey.TargetTable)
            .Where(index => index.Unique)
            .FirstOrDefault(index => GetIndexColumns(connection, transaction, index.Name)
                .SequenceEqual(foreignKey.ToColumns, StringComparer.Ordinal));
        if (parentIndex is null)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite foreign key {childTable} does not reference an exact unique parent key.");
        }

        var parentCollations = GetIndexCollations(connection, transaction, parentIndex.Name);
        var childCollations = foreignKey.FromColumns
            .Select(column => GetDeclaredColumnCollation(connection, transaction, childTable, column))
            .ToArray();
        if (parentCollations.Count != foreignKey.ToColumns.Count ||
            childCollations.Length != parentCollations.Count ||
            childCollations.Where((collation, index) => !string.Equals(collation, parentCollations[index], StringComparison.OrdinalIgnoreCase)).Any())
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite foreign key {childTable} has incompatible column collations.");
        }
    }

    private static IReadOnlyList<string> GetIndexCollations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string indexName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT seqno, coll FROM pragma_index_xinfo($indexName) WHERE key = 1 ORDER BY seqno;";
        command.Parameters.AddWithValue("$indexName", indexName);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.IsDBNull(1) ? "BINARY" : reader.GetString(1));
        }

        return result;
    }

    private static string GetDeclaredColumnCollation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        var sql = command.ExecuteScalar() as string ?? string.Empty;
        var columnPattern = $"(?im)(?:^|\\(|,)\\s*{Regex.Escape(columnName)}\\s+.*?(?:COLLATE\\s+(?<collation>[A-Za-z0-9_]+))?(?=,|\\r?$)";
        var match = Regex.Match(sql, columnPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        return match.Groups["collation"].Success
            ? match.Groups["collation"].Value
            : "BINARY";
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

    private static void ValidateTrigger(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SqliteTriggerDefinition expected)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT tbl_name, sql FROM sqlite_master WHERE type = 'trigger' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", expected.Name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite schema is missing required trigger {expected.Name}.");
        }

        var tableName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var sql = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var normalized = Regex.Replace(sql, @"\s+", " ").Trim();
        var matchesShape = expected.CompleteDefinitionPattern is not null
            ? expected.CompleteDefinitionPattern.IsMatch(normalized)
            : expected.RequiredPatterns.All(pattern => pattern.IsMatch(normalized));
        if (!string.Equals(tableName, expected.TableName, StringComparison.Ordinal) || !matchesShape)
        {
            throw new SqliteOperationalStoreException(
                "sqlite_corrupt",
                $"SQLite schema trigger {expected.Name} does not match the compiled immutable-trigger semantics.");
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
    IReadOnlyList<string> Columns,
    Regex? PredicatePattern = null,
    Regex? DefinitionPattern = null);

internal sealed record SqliteUniqueConstraintDefinition(
    string TableName,
    IReadOnlyList<string> Columns);

internal sealed record SqliteForeignKeyDefinition(
    string TableName,
    IReadOnlyList<SqliteForeignKeyInfo> ForeignKeys,
    bool RequireCompatibleCollation = false);

internal sealed record SqliteDeferredForeignKeyDefinition(string TableName, Regex Pattern);

internal sealed record SqliteCheckConstraintDefinition(string TableName, Regex Pattern);

internal sealed record SqliteTriggerDefinition(
    string Name,
    string TableName,
    IReadOnlyList<Regex> RequiredPatterns,
    Regex? CompleteDefinitionPattern = null);

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

internal static class SqliteSchemaV3
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(3, "schema_v3_setup_intents"),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("setup_intents", new[]
        {
            new SqliteColumnDefinition("intent_id", "TEXT", true, 1),
            new SqliteColumnDefinition("intent_kind_code", "TEXT", true, 0),
            new SqliteColumnDefinition("idempotency_key", "TEXT", true, 0),
            new SqliteColumnDefinition("request_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("current_user_sid", "TEXT", true, 0),
            new SqliteColumnDefinition("session_binding", "TEXT", true, 0),
            new SqliteColumnDefinition("status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("requested_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("expires_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("plan_id", "TEXT", false, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", false, 0),
            new SqliteColumnDefinition("lease_id", "TEXT", false, 0),
            new SqliteColumnDefinition("scope_id", "TEXT", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
            new SqliteColumnDefinition("terminal_reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("scheduled_start_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("latest_start_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("planned_end_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("maximum_duration_ms", "INTEGER", false, 0),
            new SqliteColumnDefinition("lease_valid_until_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("output_directory", "TEXT", false, 0),
            new SqliteColumnDefinition("frozen_file_name", "TEXT", false, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_setup_intents_status_expires", "setup_intents", false, new[] { "status_code", "expires_at_utc" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("setup_intents", new[] { "intent_kind_code", "idempotency_key", "current_user_sid", "session_binding" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check(@"intent_id\s+TEXT\s+NOT NULL\s+PRIMARY KEY\s+CHECK\s*\(length\(trim\(intent_id\)\)\s*>\s*0\s+AND\s+length\(intent_id\)\s*<=\s*128\s+AND\s+instr\(intent_id,\s*char\(47\)\)\s*=\s*0\s+AND\s+instr\(intent_id,\s*char\(92\)\)\s*=\s*0\)"),
        Check(@"intent_kind_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(intent_kind_code\s*=\s*'standing_once_fixed_region'\)"),
        Check(@"idempotency_key\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(idempotency_key\)\)\s*>\s*0\s+AND\s+length\(idempotency_key\)\s*<=\s*128\s+AND\s+instr\(idempotency_key,\s*char\(47\)\)\s*=\s*0\s+AND\s+instr\(idempotency_key,\s*char\(92\)\)\s*=\s*0\)"),
        Check(@"request_digest\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(request_digest\)\s*=\s*64\s+AND\s+request_digest\s*=\s*lower\(request_digest\)\s+AND\s+request_digest\s+NOT GLOB\s+'\*\[\^0-9a-f\]\*'\)"),
        Check(@"current_user_sid\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(current_user_sid\)\)\s*>\s*0\s+AND\s+length\(current_user_sid\)\s*<=\s*256\)"),
        Check(@"session_binding\s+TEXT\s+NOT NULL\s+CHECK\s*\(length\(trim\(session_binding\)\)\s*>\s*0\s+AND\s+length\(session_binding\)\s*<=\s*256\)"),
        Check(@"status_code\s+TEXT\s+NOT NULL\s+CHECK\s*\(status_code\s+IN\s*\('region_selection_pending',\s*'lease_approval_pending',\s*'activated',\s*'rejected',\s*'expired'\)\)"),
        Check(@"requested_at_utc\s+INTEGER\s+NOT NULL\s+CHECK\s*\(requested_at_utc\s*>=\s*0\)"),
        Check(@"expires_at_utc\s+INTEGER\s+NOT NULL\s+CHECK\s*\(expires_at_utc\s*>\s*requested_at_utc\)"),
        Check(@"plan_id\s+TEXT\s+NULL\s+CHECK\s*\(plan_id\s+IS\s+NULL\s+OR\s+\(length\(trim\(plan_id\)\)\s*>\s*0\s+AND\s+length\(plan_id\)\s*<=\s*128\s+AND\s+instr\(plan_id,\s*char\(47\)\)\s*=\s*0\s+AND\s+instr\(plan_id,\s*char\(92\)\)\s*=\s*0\)\)"),
        Check(@"occurrence_id\s+TEXT\s+NULL\s+CHECK\s*\(occurrence_id\s+IS\s+NULL\s+OR\s+\(length\(trim\(occurrence_id\)\)\s*>\s*0\s+AND\s+length\(occurrence_id\)\s*<=\s*128\s+AND\s+instr\(occurrence_id,\s*char\(47\)\)\s*=\s*0\s+AND\s+instr\(occurrence_id,\s*char\(92\)\)\s*=\s*0\)\)"),
        Check(@"lease_id\s+TEXT\s+NULL\s+CHECK\s*\(lease_id\s+IS\s+NULL\s+OR\s+\(length\(trim\(lease_id\)\)\s*>\s*0\s+AND\s+length\(lease_id\)\s*<=\s*128\s+AND\s+instr\(lease_id,\s*char\(47\)\)\s*=\s*0\s+AND\s+instr\(lease_id,\s*char\(92\)\)\s*=\s*0\)\)"),
        Check(@"scope_id\s+TEXT\s+NULL\s+CHECK\s*\(scope_id\s+IS\s+NULL\s+OR\s+\(length\(trim\(scope_id\)\)\s*>\s*0\s+AND\s+length\(scope_id\)\s*<=\s*128\s+AND\s+instr\(scope_id,\s*char\(47\)\)\s*=\s*0\s+AND\s+instr\(scope_id,\s*char\(92\)\)\s*=\s*0\)\)"),
        Check(@"created_at_utc\s+INTEGER\s+NOT NULL\s+CHECK\s*\(created_at_utc\s*>=\s*0\)"),
        Check(@"updated_at_utc\s+INTEGER\s+NOT NULL\s+CHECK\s*\(updated_at_utc\s*>=\s*created_at_utc\)"),
        Check(@"version\s+INTEGER\s+NOT NULL\s+CHECK\s*\(version\s*>=\s*0\)"),
        Check(@"terminal_reason_code\s+TEXT\s+NULL\s+CHECK\s*\(\(status_code\s+IN\s*\('region_selection_pending',\s*'lease_approval_pending',\s*'activated'\)\s+AND\s+terminal_reason_code\s+IS\s+NULL\)\s+OR\s+\(status_code\s+IN\s*\('rejected',\s*'expired'\)\s+AND\s+terminal_reason_code\s+IS\s+NOT\s+NULL\s+AND\s+length\(trim\(terminal_reason_code\)\)\s*>\s*0\)\)"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE setup_intents (
            intent_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(intent_id)) > 0 AND length(intent_id) <= 128 AND instr(intent_id, char(47)) = 0 AND instr(intent_id, char(92)) = 0),
            intent_kind_code TEXT NOT NULL CHECK (intent_kind_code = 'standing_once_fixed_region'),
            idempotency_key TEXT NOT NULL CHECK (length(trim(idempotency_key)) > 0 AND length(idempotency_key) <= 128 AND instr(idempotency_key, char(47)) = 0 AND instr(idempotency_key, char(92)) = 0),
            request_digest TEXT NOT NULL CHECK (length(request_digest) = 64 AND request_digest = lower(request_digest) AND request_digest NOT GLOB '*[^0-9a-f]*'),
            current_user_sid TEXT NOT NULL CHECK (length(trim(current_user_sid)) > 0 AND length(current_user_sid) <= 256),
            session_binding TEXT NOT NULL CHECK (length(trim(session_binding)) > 0 AND length(session_binding) <= 256),
            status_code TEXT NOT NULL CHECK (status_code IN ('region_selection_pending', 'lease_approval_pending', 'activated', 'rejected', 'expired')),
            requested_at_utc INTEGER NOT NULL CHECK (requested_at_utc >= 0),
            expires_at_utc INTEGER NOT NULL CHECK (expires_at_utc > requested_at_utc),
            plan_id TEXT NULL CHECK (plan_id IS NULL OR (length(trim(plan_id)) > 0 AND length(plan_id) <= 128 AND instr(plan_id, char(47)) = 0 AND instr(plan_id, char(92)) = 0)),
            occurrence_id TEXT NULL CHECK (occurrence_id IS NULL OR (length(trim(occurrence_id)) > 0 AND length(occurrence_id) <= 128 AND instr(occurrence_id, char(47)) = 0 AND instr(occurrence_id, char(92)) = 0)),
            lease_id TEXT NULL CHECK (lease_id IS NULL OR (length(trim(lease_id)) > 0 AND length(lease_id) <= 128 AND instr(lease_id, char(47)) = 0 AND instr(lease_id, char(92)) = 0)),
            scope_id TEXT NULL CHECK (scope_id IS NULL OR (length(trim(scope_id)) > 0 AND length(scope_id) <= 128 AND instr(scope_id, char(47)) = 0 AND instr(scope_id, char(92)) = 0)),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            terminal_reason_code TEXT NULL CHECK ((status_code IN ('region_selection_pending', 'lease_approval_pending', 'activated') AND terminal_reason_code IS NULL) OR (status_code IN ('rejected', 'expired') AND terminal_reason_code IS NOT NULL AND length(trim(terminal_reason_code)) > 0)),
            UNIQUE (intent_kind_code, idempotency_key, current_user_sid, session_binding)
        );

        CREATE INDEX idx_setup_intents_status_expires
            ON setup_intents(status_code, expires_at_utc);
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("setup_intents", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, SqliteSchemaV1.ComputeChecksum(canonicalDefinition));
    }
}

internal static class SqliteSchemaV4
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(4, "schema_v4_setup_intent_request_metadata"),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.Empty<SqliteTableDefinition>();
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.Empty<SqliteIndexDefinition>();
    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.Empty<SqliteUniqueConstraintDefinition>();
    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();
    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.Empty<SqliteCheckConstraintDefinition>();

    private const string SchemaDefinition = """
        ALTER TABLE setup_intents ADD COLUMN scheduled_start_utc INTEGER NULL CHECK (scheduled_start_utc IS NULL OR scheduled_start_utc >= 0);
        ALTER TABLE setup_intents ADD COLUMN latest_start_utc INTEGER NULL CHECK (latest_start_utc IS NULL OR latest_start_utc >= 0);
        ALTER TABLE setup_intents ADD COLUMN planned_end_utc INTEGER NULL CHECK (planned_end_utc IS NULL OR planned_end_utc >= 0);
        ALTER TABLE setup_intents ADD COLUMN maximum_duration_ms INTEGER NULL CHECK (maximum_duration_ms IS NULL OR (maximum_duration_ms > 0 AND maximum_duration_ms <= 600000));
        ALTER TABLE setup_intents ADD COLUMN lease_valid_until_utc INTEGER NULL CHECK (lease_valid_until_utc IS NULL OR lease_valid_until_utc >= 0);
        ALTER TABLE setup_intents ADD COLUMN output_directory TEXT NULL CHECK (output_directory IS NULL OR length(trim(output_directory)) > 0);
        ALTER TABLE setup_intents ADD COLUMN frozen_file_name TEXT NULL CHECK (frozen_file_name IS NULL OR (length(trim(frozen_file_name)) > 0 AND instr(frozen_file_name, char(47)) = 0 AND instr(frozen_file_name, char(92)) = 0));
        """;

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, SqliteSchemaV1.ComputeChecksum(canonicalDefinition));
    }
}

internal static class SqliteSchemaV5
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(5, "schema_v5_standing_lease_safety_controls"),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("unattended_safety_state", new[]
        {
            new SqliteColumnDefinition("state_id", "TEXT", true, 1),
            new SqliteColumnDefinition("unattended_mode_code", "TEXT", true, 0),
            new SqliteColumnDefinition("mode_changed_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("unattended_enabled_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("stop_all_applied", "INTEGER", true, 0),
            new SqliteColumnDefinition("stop_all_operation_id", "TEXT", false, 0),
            new SqliteColumnDefinition("stop_all_reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("stop_all_requested_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("stop_all_applied_at_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("standing_lease_safety_operations", new[]
        {
            new SqliteColumnDefinition("operation_id", "TEXT", true, 1),
            new SqliteColumnDefinition("operation_kind_code", "TEXT", true, 0),
            new SqliteColumnDefinition("intent_id", "TEXT", false, 0),
            new SqliteColumnDefinition("lease_id", "TEXT", false, 0),
            new SqliteColumnDefinition("requested_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("reason_code", "TEXT", true, 0),
            new SqliteColumnDefinition("result_code", "TEXT", true, 0),
            new SqliteColumnDefinition("changed", "INTEGER", true, 0),
            new SqliteColumnDefinition("requires_active_run_stop", "INTEGER", true, 0),
            new SqliteColumnDefinition("completed_at_utc", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_standing_lease_safety_operations_kind_time", "standing_lease_safety_operations", false, new[] { "operation_kind_code", "requested_at_utc" }),
        new SqliteIndexDefinition("idx_standing_lease_safety_operations_lease_time", "standing_lease_safety_operations", false, new[] { "lease_id", "requested_at_utc" }),
        new SqliteIndexDefinition("idx_standing_lease_safety_operations_intent_time", "standing_lease_safety_operations", false, new[] { "intent_id", "requested_at_utc" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.Empty<SqliteUniqueConstraintDefinition>();
    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();
    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("state_id\\s+TEXT\\s+NOT NULL\\s+PRIMARY KEY\\s+CHECK\\s*\\(state_id\\s*=\\s*'global'\\)"),
        Check("unattended_mode_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(unattended_mode_code\\s+IN\\s*\\('enabled',\\s*'disabled'\\)\\)"),
        Check("mode_changed_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(mode_changed_at_utc\\s*>=\\s*0\\)"),
        Check("unattended_enabled_at_utc\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(unattended_enabled_at_utc\\s+IS NULL\\s+OR\\s+unattended_enabled_at_utc\\s*>=\\s*0\\)"),
        Check("stop_all_applied\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(stop_all_applied\\s+IN\\s*\\(0,\\s*1\\)\\)"),
        Check("stop_all_operation_id\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(stop_all_operation_id\\s+IS NULL\\s+OR\\s+\\(length\\(trim\\(stop_all_operation_id\\)\\)\\s*>\\s*0\\s+AND\\s+instr\\(stop_all_operation_id,\\s*char\\(47\\)\\)\\s*=\\s*0\\s+AND\\s+instr\\(stop_all_operation_id,\\s*char\\(92\\)\\)\\s*=\\s*0\\)\\)"),
        Check("stop_all_reason_code\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(stop_all_reason_code\\s+IS NULL\\s+OR\\s+length\\(trim\\(stop_all_reason_code\\)\\)\\s*>\\s*0\\)"),
        Check("stop_all_requested_at_utc\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(stop_all_requested_at_utc\\s+IS NULL\\s+OR\\s+stop_all_requested_at_utc\\s*>=\\s*0\\)"),
        Check("stop_all_applied_at_utc\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(stop_all_applied_at_utc\\s+IS NULL\\s+OR\\s+stop_all_applied_at_utc\\s*>=\\s*0\\)"),
        Check("CHECK\\s*\\(\\(stop_all_applied\\s*=\\s*0\\s+AND\\s+stop_all_operation_id\\s+IS NULL\\s+AND\\s+stop_all_reason_code\\s+IS NULL\\s+AND\\s+stop_all_requested_at_utc\\s+IS NULL\\s+AND\\s+stop_all_applied_at_utc\\s+IS NULL\\)\\s+OR\\s+\\(stop_all_applied\\s*=\\s*1\\s+AND\\s+stop_all_operation_id\\s+IS NOT NULL\\s+AND\\s+stop_all_reason_code\\s+IS NOT NULL\\s+AND\\s+stop_all_requested_at_utc\\s+IS NOT NULL\\s+AND\\s+stop_all_applied_at_utc\\s+IS NOT NULL\\)\\)"),
        Check("version\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(version\\s*>=\\s*0\\)"),
        CheckOperations("operation_id\\s+TEXT\\s+NOT NULL\\s+PRIMARY KEY\\s+CHECK\\s*\\(length\\(trim\\(operation_id\\)\\)\\s*>\\s*0\\s+AND\\s+length\\(operation_id\\)\\s*<=\\s*128\\s+AND\\s+instr\\(operation_id,\\s*char\\(47\\)\\)\\s*=\\s*0\\s+AND\\s+instr\\(operation_id,\\s*char\\(92\\)\\)\\s*=\\s*0\\)"),
        CheckOperations("operation_kind_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(operation_kind_code\\s+IN\\s*\\('lease_revoke',\\s*'stop_all_revoke_all',\\s*'unattended_enable',\\s*'unattended_disable'\\)\\)"),
        CheckOperations("intent_id\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(intent_id\\s+IS NULL\\s+OR\\s+\\(length\\(trim\\(intent_id\\)\\)\\s*>\\s*0\\s+AND\\s+length\\(intent_id\\)\\s*<=\\s*128\\s+AND\\s+instr\\(intent_id,\\s*char\\(47\\)\\)\\s*=\\s*0\\s+AND\\s+instr\\(intent_id,\\s*char\\(92\\)\\)\\s*=\\s*0\\)\\)"),
        CheckOperations("lease_id\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(lease_id\\s+IS NULL\\s+OR\\s+\\(length\\(trim\\(lease_id\\)\\)\\s*>\\s*0\\s+AND\\s+length\\(lease_id\\)\\s*<=\\s*128\\s+AND\\s+instr\\(lease_id,\\s*char\\(47\\)\\)\\s*=\\s*0\\s+AND\\s+instr\\(lease_id,\\s*char\\(92\\)\\)\\s*=\\s*0\\)\\)"),
        CheckOperations("requested_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(requested_at_utc\\s*>=\\s*0\\)"),
        CheckOperations("reason_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(reason_code\\)\\)\\s*>\\s*0\\)"),
        CheckOperations("result_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(result_code\\)\\)\\s*>\\s*0\\)"),
        CheckOperations("changed\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(changed\\s+IN\\s*\\(0,\\s*1\\)\\)"),
        CheckOperations("requires_active_run_stop\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(requires_active_run_stop\\s+IN\\s*\\(0,\\s*1\\)\\)"),
        CheckOperations("completed_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(completed_at_utc\\s*>=\\s*requested_at_utc\\)"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE unattended_safety_state (
            state_id TEXT NOT NULL PRIMARY KEY CHECK (state_id = 'global'),
            unattended_mode_code TEXT NOT NULL CHECK (unattended_mode_code IN ('enabled', 'disabled')),
            mode_changed_at_utc INTEGER NOT NULL CHECK (mode_changed_at_utc >= 0),
            unattended_enabled_at_utc INTEGER NULL CHECK (unattended_enabled_at_utc IS NULL OR unattended_enabled_at_utc >= 0),
            stop_all_applied INTEGER NOT NULL CHECK (stop_all_applied IN (0, 1)),
            stop_all_operation_id TEXT NULL CHECK (stop_all_operation_id IS NULL OR (length(trim(stop_all_operation_id)) > 0 AND instr(stop_all_operation_id, char(47)) = 0 AND instr(stop_all_operation_id, char(92)) = 0)),
            stop_all_reason_code TEXT NULL CHECK (stop_all_reason_code IS NULL OR length(trim(stop_all_reason_code)) > 0),
            stop_all_requested_at_utc INTEGER NULL CHECK (stop_all_requested_at_utc IS NULL OR stop_all_requested_at_utc >= 0),
            stop_all_applied_at_utc INTEGER NULL CHECK (stop_all_applied_at_utc IS NULL OR stop_all_applied_at_utc >= 0),
            version INTEGER NOT NULL CHECK (version >= 0),
            CHECK ((stop_all_applied = 0 AND stop_all_operation_id IS NULL AND stop_all_reason_code IS NULL AND stop_all_requested_at_utc IS NULL AND stop_all_applied_at_utc IS NULL) OR (stop_all_applied = 1 AND stop_all_operation_id IS NOT NULL AND stop_all_reason_code IS NOT NULL AND stop_all_requested_at_utc IS NOT NULL AND stop_all_applied_at_utc IS NOT NULL))
        );

        INSERT INTO unattended_safety_state (state_id, unattended_mode_code, mode_changed_at_utc, unattended_enabled_at_utc, stop_all_applied, stop_all_operation_id, stop_all_reason_code, stop_all_requested_at_utc, stop_all_applied_at_utc, version)
        VALUES ('global', 'enabled', 0, 0, 0, NULL, NULL, NULL, NULL, 0);

        CREATE TABLE standing_lease_safety_operations (
            operation_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(operation_id)) > 0 AND length(operation_id) <= 128 AND instr(operation_id, char(47)) = 0 AND instr(operation_id, char(92)) = 0),
            operation_kind_code TEXT NOT NULL CHECK (operation_kind_code IN ('lease_revoke', 'stop_all_revoke_all', 'unattended_enable', 'unattended_disable')),
            intent_id TEXT NULL CHECK (intent_id IS NULL OR (length(trim(intent_id)) > 0 AND length(intent_id) <= 128 AND instr(intent_id, char(47)) = 0 AND instr(intent_id, char(92)) = 0)),
            lease_id TEXT NULL CHECK (lease_id IS NULL OR (length(trim(lease_id)) > 0 AND length(lease_id) <= 128 AND instr(lease_id, char(47)) = 0 AND instr(lease_id, char(92)) = 0)),
            requested_at_utc INTEGER NOT NULL CHECK (requested_at_utc >= 0),
            reason_code TEXT NOT NULL CHECK (length(trim(reason_code)) > 0),
            result_code TEXT NOT NULL CHECK (length(trim(result_code)) > 0),
            changed INTEGER NOT NULL CHECK (changed IN (0, 1)),
            requires_active_run_stop INTEGER NOT NULL CHECK (requires_active_run_stop IN (0, 1)),
            completed_at_utc INTEGER NOT NULL CHECK (completed_at_utc >= requested_at_utc)
        );

        CREATE INDEX idx_standing_lease_safety_operations_kind_time
            ON standing_lease_safety_operations(operation_kind_code, requested_at_utc);
        CREATE INDEX idx_standing_lease_safety_operations_lease_time
            ON standing_lease_safety_operations(lease_id, requested_at_utc);
        CREATE INDEX idx_standing_lease_safety_operations_intent_time
            ON standing_lease_safety_operations(intent_id, requested_at_utc);
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("unattended_safety_state", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteCheckConstraintDefinition CheckOperations(string pattern) =>
        new("standing_lease_safety_operations", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, SqliteSchemaV1.ComputeChecksum(canonicalDefinition));
    }
}

/// <summary>
/// The v5 safety row was intentionally seeded enabled while the safety-control
/// product surface was still being developed.  v6 changes only that original
/// seed shape to the safe default.  It must not rewrite an explicit user
/// choice or any later safety operation.
/// </summary>
internal static class SqliteSchemaV6
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(6, "schema_v6_unattended_default_disabled"),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.Empty<SqliteTableDefinition>();
    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.Empty<SqliteIndexDefinition>();
    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.Empty<SqliteUniqueConstraintDefinition>();
    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();
    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();
    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.Empty<SqliteCheckConstraintDefinition>();

    private const string SchemaDefinition = """
        UPDATE unattended_safety_state
        SET unattended_mode_code = 'disabled'
        WHERE state_id = 'global'
          AND unattended_mode_code = 'enabled'
          AND mode_changed_at_utc = 0
          AND unattended_enabled_at_utc = 0
          AND stop_all_applied = 0
          AND stop_all_operation_id IS NULL
          AND stop_all_reason_code IS NULL
          AND stop_all_requested_at_utc IS NULL
          AND stop_all_applied_at_utc IS NULL
          AND version = 0;
        """;

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, SqliteSchemaV1.ComputeChecksum(canonicalDefinition));
    }
}

internal static class SqliteSchemaV7
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(7, "schema_v7_recurring_schedule_versions_and_occurrence_slots"),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recurring_schedule_versions", new[]
        {
            new SqliteColumnDefinition("plan_id", "TEXT", true, 1),
            new SqliteColumnDefinition("schedule_revision", "INTEGER", true, 2),
            new SqliteColumnDefinition("schedule_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("schedule_kind_code", "TEXT", true, 0),
            new SqliteColumnDefinition("time_zone_id", "TEXT", true, 0),
            new SqliteColumnDefinition("time_zone_rules_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("local_start_date", "TEXT", true, 0),
            new SqliteColumnDefinition("local_end_date", "TEXT", true, 0),
            new SqliteColumnDefinition("local_wall_clock_seconds", "INTEGER", true, 0),
            new SqliteColumnDefinition("weekday_mask", "INTEGER", true, 0),
            new SqliteColumnDefinition("maximum_occurrences", "INTEGER", true, 0),
            new SqliteColumnDefinition("recording_duration_ticks", "INTEGER", true, 0),
            new SqliteColumnDefinition("latest_start_grace_ticks", "INTEGER", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("recurring_occurrence_slots", new[]
        {
            new SqliteColumnDefinition("occurrence_identity", "TEXT", true, 1),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("schedule_revision", "INTEGER", true, 0),
            new SqliteColumnDefinition("schedule_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("local_date", "TEXT", true, 0),
            new SqliteColumnDefinition("local_wall_clock_seconds", "INTEGER", true, 0),
            new SqliteColumnDefinition("time_zone_id", "TEXT", true, 0),
            new SqliteColumnDefinition("slot_status_code", "TEXT", true, 0),
            new SqliteColumnDefinition("scheduled_start_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("latest_start_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("planned_end_utc", "INTEGER", false, 0),
            new SqliteColumnDefinition("resolution_code", "TEXT", true, 0),
            new SqliteColumnDefinition("terminal_reason_code", "TEXT", false, 0),
            new SqliteColumnDefinition("occurrence_id", "TEXT", false, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_recurring_occurrence_slots_plan_status_date", "recurring_occurrence_slots", false, new[] { "plan_id", "slot_status_code", "local_date" }),
        new SqliteIndexDefinition("idx_recurring_occurrence_slots_occurrence", "recurring_occurrence_slots", false, new[] { "occurrence_id" }),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("recurring_schedule_versions", new[] { "plan_id", "schedule_digest", "time_zone_rules_digest" }),
        new SqliteUniqueConstraintDefinition("recurring_schedule_versions", new[] { "plan_id", "schedule_revision", "schedule_digest" }),
        new SqliteUniqueConstraintDefinition("recurring_occurrence_slots", new[] { "plan_id", "schedule_revision", "local_date", "local_wall_clock_seconds" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("recurring_schedule_versions", new[]
        {
            new SqliteForeignKeyInfo("plans", "NO ACTION", "RESTRICT", new[] { "plan_id" }, new[] { "id" }),
        }),
        new SqliteForeignKeyDefinition("recurring_occurrence_slots", new[]
        {
            new SqliteForeignKeyInfo("recurring_schedule_versions", "NO ACTION", "RESTRICT", new[] { "plan_id", "schedule_revision", "schedule_digest" }, new[] { "plan_id", "schedule_revision", "schedule_digest" }),
            new SqliteForeignKeyInfo("plan_occurrences", "NO ACTION", "RESTRICT", new[] { "occurrence_id", "plan_id" }, new[] { "id", "plan_id" }),
        }),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        CheckSchedule("plan_id\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(plan_id\\)\\)\\s*>\\s*0\\)"),
        CheckSchedule("schedule_revision\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(schedule_revision\\s*>\\s*0\\)"),
        CheckSchedule("schedule_digest\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(schedule_digest\\)\\s*=\\s*86.*substr\\(schedule_digest,\\s*1,\\s*22\\)\\s*=\\s*'recurring-schedule/v1:'.*substr\\(schedule_digest,\\s*23\\)\\s+NOT GLOB"),
        CheckSchedule("schedule_kind_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(schedule_kind_code\\s+IN\\s*\\('daily',\\s*'weekly'\\)\\)"),
        CheckSchedule("time_zone_rules_digest\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(time_zone_rules_digest\\)\\s*=\\s*92.*substr\\(time_zone_rules_digest,\\s*1,\\s*28\\)\\s*=\\s*'recurring-timezone-rules/v1:'.*substr\\(time_zone_rules_digest,\\s*29\\)\\s+NOT GLOB"),
        CheckSchedule("local_start_date\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(local_start_date\\)\\s*=\\s*10"),
        CheckSchedule("local_end_date\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(local_end_date\\)\\s*=\\s*10"),
        CheckSchedule("CHECK\\s*\\(local_end_date\\s+>=\\s+local_start_date\\)"),
        CheckSchedule("local_wall_clock_seconds\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(local_wall_clock_seconds\\s+BETWEEN\\s+0\\s+AND\\s+86399\\)"),
        CheckSchedule("weekday_mask\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(weekday_mask\\s+BETWEEN\\s+0\\s+AND\\s+127\\)"),
        CheckSchedule("CHECK\\s*\\(\\(schedule_kind_code\\s*=\\s*'daily'.*weekday_mask\\s*=\\s*0\\).*schedule_kind_code\\s*=\\s*'weekly'.*weekday_mask\\s+BETWEEN\\s+1\\s+AND\\s+127"),
        CheckSchedule("maximum_occurrences\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(maximum_occurrences\\s*>\\s*0\\)"),
        CheckSchedule("recording_duration_ticks\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(recording_duration_ticks\\s*>\\s*0\\)"),
        CheckSchedule("latest_start_grace_ticks\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(latest_start_grace_ticks\\s+BETWEEN\\s+0\\s+AND\\s+3000000000\\)"),
        CheckSchedule("created_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(created_at_utc\\s*>=\\s*0\\)"),
        CheckSlot("occurrence_identity\\s+TEXT\\s+NOT NULL\\s+PRIMARY KEY\\s+CHECK\\s*\\(length\\(occurrence_identity\\)\\s*=\\s*88.*substr\\(occurrence_identity,\\s*1,\\s*24\\)\\s*=\\s*'recurring-occurrence/v1:'.*substr\\(occurrence_identity,\\s*25\\)\\s+NOT GLOB"),
        CheckSlot("schedule_revision\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(schedule_revision\\s*>\\s*0\\)"),
        CheckSlot("schedule_digest\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(schedule_digest\\)\\s*=\\s*86.*substr\\(schedule_digest,\\s*1,\\s*22\\)\\s*=\\s*'recurring-schedule/v1:'.*substr\\(schedule_digest,\\s*23\\)\\s+NOT GLOB"),
        CheckSlot("local_date\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(local_date\\)\\s*=\\s*10"),
        CheckSlot("local_wall_clock_seconds\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(local_wall_clock_seconds\\s+BETWEEN\\s+0\\s+AND\\s+86399\\)"),
        CheckSlot("slot_status_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(slot_status_code\\s+IN\\s*\\('scheduled',\\s*'skipped'\\)\\)"),
        CheckSlot("scheduled_start_utc\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(scheduled_start_utc\\s+IS NULL\\s+OR\\s+scheduled_start_utc\\s*>=\\s*0\\)"),
        CheckSlot("latest_start_utc\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(latest_start_utc\\s+IS NULL\\s+OR\\s+latest_start_utc\\s*>=\\s*0\\)"),
        CheckSlot("planned_end_utc\\s+INTEGER\\s+NULL\\s+CHECK\\s*\\(planned_end_utc\\s+IS NULL\\s+OR\\s+planned_end_utc\\s*>=\\s*0\\)"),
        CheckSlot("created_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(created_at_utc\\s*>=\\s*0\\)"),
        CheckSlot("CHECK\\s*\\(.*slot_status_code\\s*=\\s*'scheduled'.*terminal_reason_code\\s+IS NULL.*occurrence_id\\s+IS NOT NULL"),
        CheckSlot("CHECK\\s*\\(.*slot_status_code\\s*=\\s*'skipped'.*terminal_reason_code\\s*=\\s*'schedule_local_time_invalid'.*occurrence_id\\s+IS NULL"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE recurring_schedule_versions (
            plan_id TEXT NOT NULL CHECK (length(trim(plan_id)) > 0),
            schedule_revision INTEGER NOT NULL CHECK (schedule_revision > 0),
            schedule_digest TEXT NOT NULL CHECK (length(schedule_digest) = 86 AND substr(schedule_digest, 1, 22) = 'recurring-schedule/v1:' AND substr(schedule_digest, 23) NOT GLOB '*[^0-9a-f]*' AND substr(schedule_digest, 23) = lower(substr(schedule_digest, 23))),
            schedule_kind_code TEXT NOT NULL CHECK (schedule_kind_code IN ('daily', 'weekly')),
            time_zone_id TEXT NOT NULL CHECK (length(trim(time_zone_id)) > 0),
            time_zone_rules_digest TEXT NOT NULL CHECK (length(time_zone_rules_digest) = 92 AND substr(time_zone_rules_digest, 1, 28) = 'recurring-timezone-rules/v1:' AND substr(time_zone_rules_digest, 29) NOT GLOB '*[^0-9a-f]*' AND substr(time_zone_rules_digest, 29) = lower(substr(time_zone_rules_digest, 29))),
            local_start_date TEXT NOT NULL CHECK (length(local_start_date) = 10 AND substr(local_start_date, 5, 1) = '-' AND substr(local_start_date, 8, 1) = '-'),
            local_end_date TEXT NOT NULL CHECK (length(local_end_date) = 10 AND substr(local_end_date, 5, 1) = '-' AND substr(local_end_date, 8, 1) = '-') CHECK (local_end_date >= local_start_date),
            local_wall_clock_seconds INTEGER NOT NULL CHECK (local_wall_clock_seconds BETWEEN 0 AND 86399),
            weekday_mask INTEGER NOT NULL CHECK (weekday_mask BETWEEN 0 AND 127),
            maximum_occurrences INTEGER NOT NULL CHECK (maximum_occurrences > 0),
            recording_duration_ticks INTEGER NOT NULL CHECK (recording_duration_ticks > 0),
            latest_start_grace_ticks INTEGER NOT NULL CHECK (latest_start_grace_ticks BETWEEN 0 AND 3000000000),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            CHECK ((schedule_kind_code = 'daily' AND weekday_mask = 0) OR (schedule_kind_code = 'weekly' AND weekday_mask BETWEEN 1 AND 127)),
            PRIMARY KEY (plan_id, schedule_revision),
            UNIQUE (plan_id, schedule_digest, time_zone_rules_digest),
            UNIQUE (plan_id, schedule_revision, schedule_digest),
            FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT
        );

        CREATE TABLE recurring_occurrence_slots (
            occurrence_identity TEXT NOT NULL PRIMARY KEY CHECK (length(occurrence_identity) = 88 AND substr(occurrence_identity, 1, 24) = 'recurring-occurrence/v1:' AND substr(occurrence_identity, 25) NOT GLOB '*[^0-9a-f]*' AND substr(occurrence_identity, 25) = lower(substr(occurrence_identity, 25))),
            plan_id TEXT NOT NULL CHECK (length(trim(plan_id)) > 0),
            schedule_revision INTEGER NOT NULL CHECK (schedule_revision > 0),
            schedule_digest TEXT NOT NULL CHECK (length(schedule_digest) = 86 AND substr(schedule_digest, 1, 22) = 'recurring-schedule/v1:' AND substr(schedule_digest, 23) NOT GLOB '*[^0-9a-f]*' AND substr(schedule_digest, 23) = lower(substr(schedule_digest, 23))),
            local_date TEXT NOT NULL CHECK (length(local_date) = 10 AND substr(local_date, 5, 1) = '-' AND substr(local_date, 8, 1) = '-'),
            local_wall_clock_seconds INTEGER NOT NULL CHECK (local_wall_clock_seconds BETWEEN 0 AND 86399),
            time_zone_id TEXT NOT NULL CHECK (length(trim(time_zone_id)) > 0),
            slot_status_code TEXT NOT NULL CHECK (slot_status_code IN ('scheduled', 'skipped')),
            scheduled_start_utc INTEGER NULL CHECK (scheduled_start_utc IS NULL OR scheduled_start_utc >= 0),
            latest_start_utc INTEGER NULL CHECK (latest_start_utc IS NULL OR latest_start_utc >= 0),
            planned_end_utc INTEGER NULL CHECK (planned_end_utc IS NULL OR planned_end_utc >= 0),
            resolution_code TEXT NOT NULL CHECK (length(resolution_code) >= 0),
            terminal_reason_code TEXT NULL CHECK (terminal_reason_code IS NULL OR length(trim(terminal_reason_code)) > 0),
            occurrence_id TEXT NULL CHECK (occurrence_id IS NULL OR length(trim(occurrence_id)) > 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            UNIQUE (plan_id, schedule_revision, local_date, local_wall_clock_seconds),
            FOREIGN KEY (plan_id, schedule_revision, schedule_digest)
                REFERENCES recurring_schedule_versions(plan_id, schedule_revision, schedule_digest)
                ON DELETE RESTRICT,
            FOREIGN KEY (occurrence_id, plan_id) REFERENCES plan_occurrences(id, plan_id)
                ON DELETE RESTRICT,
            CHECK ((slot_status_code = 'scheduled'
                AND scheduled_start_utc IS NOT NULL
                AND latest_start_utc IS NOT NULL
                AND planned_end_utc IS NOT NULL
                AND scheduled_start_utc <= latest_start_utc
                AND latest_start_utc < planned_end_utc
                AND resolution_code IN ('schedule_exact', 'schedule_ambiguous_earlier_utc')
                AND terminal_reason_code IS NULL
                AND occurrence_id IS NOT NULL)
                OR (slot_status_code = 'skipped'
                AND scheduled_start_utc IS NULL
                AND latest_start_utc IS NULL
                AND planned_end_utc IS NULL
                AND resolution_code = ''
                AND terminal_reason_code = 'schedule_local_time_invalid'
                AND occurrence_id IS NULL))
        );

        CREATE INDEX idx_recurring_occurrence_slots_plan_status_date
            ON recurring_occurrence_slots(plan_id, slot_status_code, local_date);
        CREATE INDEX idx_recurring_occurrence_slots_occurrence
            ON recurring_occurrence_slots(occurrence_id);
        """;

    private static SqliteCheckConstraintDefinition CheckSchedule(string pattern) =>
        new("recurring_schedule_versions", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteCheckConstraintDefinition CheckSlot(string pattern) =>
        new("recurring_occurrence_slots", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, SqliteSchemaV1.ComputeChecksum(canonicalDefinition));
    }
}

internal static class SqliteSchemaV8
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(8, "schema_v8_recurring_advancement_cursors_and_operations"),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recurring_schedule_cursors", new[]
        {
            new SqliteColumnDefinition("plan_id", "TEXT", true, 1),
            new SqliteColumnDefinition("schedule_revision", "INTEGER", true, 2),
            new SqliteColumnDefinition("schedule_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("time_zone_rules_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("initial_after_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("last_local_date", "TEXT", false, 0),
            new SqliteColumnDefinition("last_schedule_ordinal", "INTEGER", true, 0),
            new SqliteColumnDefinition("is_exhausted", "INTEGER", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("updated_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("version", "INTEGER", true, 0),
        }),
        new SqliteTableDefinition("recurring_advancement_operations", new[]
        {
            new SqliteColumnDefinition("operation_id", "TEXT", true, 1),
            new SqliteColumnDefinition("plan_id", "TEXT", true, 0),
            new SqliteColumnDefinition("schedule_revision", "INTEGER", true, 0),
            new SqliteColumnDefinition("request_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("expected_cursor_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("result_code", "TEXT", true, 0),
            new SqliteColumnDefinition("occurrence_identity", "TEXT", false, 0),
            new SqliteColumnDefinition("result_cursor_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition("idx_recurring_schedule_cursors_plan_exhausted_updated", "recurring_schedule_cursors", false, new[] { "plan_id", "is_exhausted", "updated_at_utc" }),
        new SqliteIndexDefinition("idx_recurring_advancement_operations_cursor", "recurring_advancement_operations", false, new[] { "plan_id", "schedule_revision", "expected_cursor_version" }),
        new SqliteIndexDefinition("idx_recurring_advancement_operations_occurrence", "recurring_advancement_operations", false, new[] { "occurrence_identity" }),
        new SqliteIndexDefinition(
            "ux_recurring_advancement_operations_cursor_transition",
            "recurring_advancement_operations",
            true,
            new[] { "plan_id", "schedule_revision", "expected_cursor_version" },
            new Regex("WHERE\\s+result_cursor_version\\s*=\\s*expected_cursor_version\\s*\\+\\s*1\\s*;?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.Empty<SqliteUniqueConstraintDefinition>();

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("recurring_schedule_cursors", new[]
        {
            new SqliteForeignKeyInfo("recurring_schedule_versions", "NO ACTION", "RESTRICT", new[] { "plan_id", "schedule_revision", "schedule_digest" }, new[] { "plan_id", "schedule_revision", "schedule_digest" }),
            new SqliteForeignKeyInfo("recurring_schedule_versions", "NO ACTION", "RESTRICT", new[] { "plan_id", "schedule_digest", "time_zone_rules_digest" }, new[] { "plan_id", "schedule_digest", "time_zone_rules_digest" }),
        }),
        new SqliteForeignKeyDefinition("recurring_advancement_operations", new[]
        {
            new SqliteForeignKeyInfo("recurring_schedule_cursors", "NO ACTION", "RESTRICT", new[] { "plan_id", "schedule_revision" }, new[] { "plan_id", "schedule_revision" }),
            new SqliteForeignKeyInfo("recurring_occurrence_slots", "NO ACTION", "RESTRICT", new[] { "occurrence_identity" }, new[] { "occurrence_identity" }),
        }),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        CheckCursor("plan_id\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(plan_id\\)\\)\\s*>\\s*0\\)"),
        CheckCursor("schedule_revision\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(schedule_revision\\s*>\\s*0\\)"),
        CheckCursor("schedule_digest\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(schedule_digest\\)\\s*=\\s*86.*substr\\(schedule_digest,\\s*1,\\s*22\\)\\s*=\\s*'recurring-schedule/v1:'.*NOT GLOB"),
        CheckCursor("time_zone_rules_digest\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(time_zone_rules_digest\\)\\s*=\\s*92.*substr\\(time_zone_rules_digest,\\s*1,\\s*28\\)\\s*=\\s*'recurring-timezone-rules/v1:'.*NOT GLOB"),
        CheckCursor("initial_after_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(initial_after_utc\\s*>=\\s*0\\)"),
        CheckCursor("last_local_date\\s+TEXT\\s+NULL\\s+CHECK"),
        CheckCursor("last_schedule_ordinal\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(last_schedule_ordinal\\s*>=\\s*0\\)"),
        CheckCursor("is_exhausted\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(is_exhausted\\s+IN\\s*\\(0,\\s*1\\)\\)"),
        CheckCursor("created_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(created_at_utc\\s*>=\\s*0\\)"),
        CheckCursor("updated_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(updated_at_utc\\s*>=\\s*created_at_utc\\)"),
        CheckCursor("version\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(version\\s*>=\\s*0\\)"),
        CheckCursor("CHECK\\s*\\(\\(last_local_date\\s+IS\\s+NULL\\s+AND\\s+last_schedule_ordinal\\s*=\\s*0\\)\\s+OR\\s+\\(last_local_date\\s+IS\\s+NOT\\s+NULL\\s+AND\\s+last_schedule_ordinal\\s*>\\s*0\\)\\)"),
        CheckOperation("operation_id\\s+TEXT\\s+NOT NULL\\s+PRIMARY KEY\\s+CHECK\\s*\\(length\\(trim\\(operation_id\\)\\)\\s*>\\s*0\\s+AND\\s+length\\(operation_id\\)\\s*<=\\s*128\\s+AND\\s+instr\\(operation_id,\\s*char\\(47\\)\\)\\s*=\\s*0\\s+AND\\s+instr\\(operation_id,\\s*char\\(92\\)\\)\\s*=\\s*0\\)"),
        CheckOperation("plan_id\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(plan_id\\)\\)\\s*>\\s*0\\)"),
        CheckOperation("schedule_revision\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(schedule_revision\\s*>\\s*0\\)"),
        CheckOperation("request_digest\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(request_digest\\)\\s*=\\s*89.*substr\\(request_digest,\\s*1,\\s*25\\)\\s*=\\s*'recurring-advancement/v1:'.*NOT GLOB"),
        CheckOperation("expected_cursor_version\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(expected_cursor_version\\s*>=\\s*0\\)"),
        CheckOperation("result_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(result_code\\s+IN\\s*\\('scheduled',\\s*'skipped',\\s*'exhausted'\\)\\)"),
        CheckOperation("occurrence_identity\\s+TEXT\\s+NULL\\s+CHECK\\s*\\(occurrence_identity\\s+IS\\s+NULL\\s+OR.*length\\(occurrence_identity\\)\\s*=\\s*88.*substr\\(occurrence_identity,\\s*1,\\s*24\\)\\s*=\\s*'recurring-occurrence/v1:'.*NOT GLOB"),
        CheckOperation("result_cursor_version\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(result_cursor_version\\s*>=\\s*0\\)"),
        CheckOperation("created_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(created_at_utc\\s*>=\\s*0\\)"),
        CheckOperation("CHECK\\s*\\(\\(result_code\\s+IN\\s*\\('scheduled',\\s*'skipped'\\)\\s+AND\\s+occurrence_identity\\s+IS\\s+NOT\\s+NULL\\)\\s+OR\\s+\\(result_code\\s*=\\s*'exhausted'\\s+AND\\s+occurrence_identity\\s+IS\\s+NULL\\)\\)"),
        CheckOperation("CHECK\\s*\\(\\(result_cursor_version\\s*=\\s*expected_cursor_version\\s*\\+\\s*1\\)\\s+OR\\s+\\(result_code\\s*=\\s*'exhausted'\\s+AND\\s+result_cursor_version\\s*=\\s*expected_cursor_version\\)\\)"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE recurring_schedule_cursors (
            plan_id TEXT NOT NULL CHECK (length(trim(plan_id)) > 0),
            schedule_revision INTEGER NOT NULL CHECK (schedule_revision > 0),
            schedule_digest TEXT NOT NULL CHECK (length(schedule_digest) = 86 AND substr(schedule_digest, 1, 22) = 'recurring-schedule/v1:' AND substr(schedule_digest, 23) NOT GLOB '*[^0-9a-f]*' AND substr(schedule_digest, 23) = lower(substr(schedule_digest, 23))),
            time_zone_rules_digest TEXT NOT NULL CHECK (length(time_zone_rules_digest) = 92 AND substr(time_zone_rules_digest, 1, 28) = 'recurring-timezone-rules/v1:' AND substr(time_zone_rules_digest, 29) NOT GLOB '*[^0-9a-f]*' AND substr(time_zone_rules_digest, 29) = lower(substr(time_zone_rules_digest, 29))),
            initial_after_utc INTEGER NOT NULL CHECK (initial_after_utc >= 0),
            last_local_date TEXT NULL CHECK (last_local_date IS NULL OR (length(last_local_date) = 10 AND substr(last_local_date, 5, 1) = '-' AND substr(last_local_date, 8, 1) = '-')),
            last_schedule_ordinal INTEGER NOT NULL CHECK (last_schedule_ordinal >= 0),
            is_exhausted INTEGER NOT NULL CHECK (is_exhausted IN (0, 1)),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            updated_at_utc INTEGER NOT NULL CHECK (updated_at_utc >= created_at_utc),
            version INTEGER NOT NULL CHECK (version >= 0),
            PRIMARY KEY (plan_id, schedule_revision),
            FOREIGN KEY (plan_id, schedule_revision, schedule_digest)
                REFERENCES recurring_schedule_versions(plan_id, schedule_revision, schedule_digest)
                ON DELETE RESTRICT,
            FOREIGN KEY (plan_id, schedule_digest, time_zone_rules_digest)
                REFERENCES recurring_schedule_versions(plan_id, schedule_digest, time_zone_rules_digest)
                ON DELETE RESTRICT,
            CHECK ((last_local_date IS NULL AND last_schedule_ordinal = 0) OR (last_local_date IS NOT NULL AND last_schedule_ordinal > 0))
        );

        CREATE TABLE recurring_advancement_operations (
            operation_id TEXT NOT NULL PRIMARY KEY CHECK (length(trim(operation_id)) > 0 AND length(operation_id) <= 128 AND instr(operation_id, char(47)) = 0 AND instr(operation_id, char(92)) = 0),
            plan_id TEXT NOT NULL CHECK (length(trim(plan_id)) > 0),
            schedule_revision INTEGER NOT NULL CHECK (schedule_revision > 0),
            request_digest TEXT NOT NULL CHECK (length(request_digest) = 89 AND substr(request_digest, 1, 25) = 'recurring-advancement/v1:' AND substr(request_digest, 26) NOT GLOB '*[^0-9a-f]*' AND substr(request_digest, 26) = lower(substr(request_digest, 26))),
            expected_cursor_version INTEGER NOT NULL CHECK (expected_cursor_version >= 0),
            result_code TEXT NOT NULL CHECK (result_code IN ('scheduled', 'skipped', 'exhausted')),
            occurrence_identity TEXT NULL CHECK (occurrence_identity IS NULL OR (length(occurrence_identity) = 88 AND substr(occurrence_identity, 1, 24) = 'recurring-occurrence/v1:' AND substr(occurrence_identity, 25) NOT GLOB '*[^0-9a-f]*' AND substr(occurrence_identity, 25) = lower(substr(occurrence_identity, 25)))),
            result_cursor_version INTEGER NOT NULL CHECK (result_cursor_version >= 0),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            FOREIGN KEY (plan_id, schedule_revision) REFERENCES recurring_schedule_cursors(plan_id, schedule_revision) ON DELETE RESTRICT,
            FOREIGN KEY (occurrence_identity) REFERENCES recurring_occurrence_slots(occurrence_identity) ON DELETE RESTRICT,
            CHECK ((result_code IN ('scheduled', 'skipped') AND occurrence_identity IS NOT NULL) OR (result_code = 'exhausted' AND occurrence_identity IS NULL)),
            CHECK ((result_cursor_version = expected_cursor_version + 1) OR (result_code = 'exhausted' AND result_cursor_version = expected_cursor_version))
        );

        CREATE INDEX idx_recurring_schedule_cursors_plan_exhausted_updated
            ON recurring_schedule_cursors(plan_id, is_exhausted, updated_at_utc);
        CREATE INDEX idx_recurring_advancement_operations_cursor
            ON recurring_advancement_operations(plan_id, schedule_revision, expected_cursor_version);
        CREATE INDEX idx_recurring_advancement_operations_occurrence
            ON recurring_advancement_operations(occurrence_identity);
        CREATE UNIQUE INDEX ux_recurring_advancement_operations_cursor_transition
            ON recurring_advancement_operations(plan_id, schedule_revision, expected_cursor_version)
            WHERE result_cursor_version = expected_cursor_version + 1;
        """;

    private static SqliteCheckConstraintDefinition CheckCursor(string pattern) =>
        new("recurring_schedule_cursors", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteCheckConstraintDefinition CheckOperation(string pattern) =>
        new("recurring_advancement_operations", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        return new SqliteMigrationDefinition(version, name, canonicalDefinition, SqliteSchemaV1.ComputeChecksum(canonicalDefinition));
    }
}

internal static class SqliteSchemaV9
{
    public const string MigrationName = "schema_v9_recurring_fixed_region_profile_versions";
    public const string MigrationChecksum = "c5f3846be6a761dbeab25824e2c8de8184e691e14738669a0ac726ff4b09d11a";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(9, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recurring_fixed_region_profile_versions", new[]
        {
            new SqliteColumnDefinition("profile_id", "TEXT", true, 1),
            new SqliteColumnDefinition("profile_version", "INTEGER", true, 2),
            new SqliteColumnDefinition("profile_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("created_at_utc", "INTEGER", true, 0),
            new SqliteColumnDefinition("target_type_code", "TEXT", true, 0),
            new SqliteColumnDefinition("rebind_policy_code", "TEXT", true, 0),
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
            new SqliteColumnDefinition("topology_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("backend_code", "TEXT", true, 0),
            new SqliteColumnDefinition("audio_mode_code", "TEXT", true, 0),
            new SqliteColumnDefinition("duration_ms", "INTEGER", true, 0),
            new SqliteColumnDefinition("countdown_seconds", "INTEGER", true, 0),
            new SqliteColumnDefinition("output_directory", "TEXT", true, 0),
            new SqliteColumnDefinition("filename_prefix", "TEXT", true, 0),
            new SqliteColumnDefinition("filename_template", "TEXT", true, 0),
            new SqliteColumnDefinition("output_conflict_policy_code", "TEXT", true, 0),
            new SqliteColumnDefinition("wake_policy_code", "TEXT", true, 0),
            new SqliteColumnDefinition("desktop_requirement_code", "TEXT", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition(
            "idx_recurring_fixed_region_profile_versions_profile_version_desc",
            "recurring_fixed_region_profile_versions",
            false,
            new[] { "profile_id", "profile_version" },
            DefinitionPattern: new Regex(
                "CREATE\\s+INDEX.*ON\\s+recurring_fixed_region_profile_versions\\s*\\(\\s*profile_id\\s*,\\s*profile_version\\s+DESC\\s*\\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled)),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.AsReadOnly(new[]
    {
        new SqliteUniqueConstraintDefinition("recurring_fixed_region_profile_versions", new[] { "profile_id", "profile_version", "profile_digest" }),
        new SqliteUniqueConstraintDefinition("recurring_fixed_region_profile_versions", new[] { "profile_digest" }),
    });

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.Empty<SqliteForeignKeyDefinition>();

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("profile_id\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(profile_id\\)\\)\\s*>\\s*0.*length\\(profile_id\\).*instr\\(profile_id,\\s*char\\(47\\)\\).*instr\\(profile_id,\\s*char\\(92\\)\\).*instr\\(profile_id,\\s*char\\(58\\)\\)"),
        Check("profile_version\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(profile_version\\s*>\\s*0\\)"),
        Check("profile_digest\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(profile_digest\\).*substr\\(profile_digest,\\s*1,\\s*length\\('recurring-fixed-region-profile/v1:'\\)\\).*NOT GLOB.*lower"),
        Check("created_at_utc\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(created_at_utc\\s*>=\\s*0\\)"),
        Check("target_type_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(target_type_code\\s*=\\s*'fixed_region'\\)"),
        Check("rebind_policy_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(rebind_policy_code\\s*=\\s*'exact_match_only'\\)"),
        Check("capture_semantics_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(capture_semantics_code\\s*=\\s*'desktop_region'\\)"),
        Check("coordinate_space_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(coordinate_space_code\\s*=\\s*'physical_virtual_screen'\\)"),
        Check("display_identity_status_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(display_identity_status_code\\s*=\\s*'resolved'\\)"),
        Check("stable_display_fingerprint\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(stable_display_fingerprint\\)\\)\\s*>\\s*0.*length\\(stable_display_fingerprint\\)\\s*<=\\s*256"),
        Check("display_bounds_x\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(display_bounds_x\\s+BETWEEN\\s+-2147483648\\s+AND\\s+2147483647\\)"),
        Check("display_bounds_y\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(display_bounds_y\\s+BETWEEN\\s+-2147483648\\s+AND\\s+2147483647\\)"),
        Check("display_bounds_width\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(display_bounds_width\\s+BETWEEN\\s+1\\s+AND\\s+2147483647\\)"),
        Check("display_bounds_height\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(display_bounds_height\\s+BETWEEN\\s+1\\s+AND\\s+2147483647\\)"),
        Check("region_x\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(region_x\\s+BETWEEN\\s+0\\s+AND\\s+2147483647\\)"),
        Check("region_y\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(region_y\\s+BETWEEN\\s+0\\s+AND\\s+2147483647\\)"),
        Check("region_width\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(region_width\\s+BETWEEN\\s+1\\s+AND\\s+2147483647\\)"),
        Check("region_height\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(region_height\\s+BETWEEN\\s+1\\s+AND\\s+2147483647\\)"),
        Check("dpi_x\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(dpi_x\\s*>\\s*0"),
        Check("dpi_y\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(dpi_y\\s*>\\s*0"),
        Check("physical_width\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(physical_width\\s*>\\s*0.*physical_width\\s*=\\s*display_bounds_width"),
        Check("physical_height\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(physical_height\\s*>\\s*0.*physical_height\\s*=\\s*display_bounds_height"),
        Check("orientation_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(orientation_code\\s+IN\\s*\\('landscape',\\s*'portrait',\\s*'landscape_flipped',\\s*'portrait_flipped'\\)\\)"),
        Check("topology_digest\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(topology_digest\\)\\s*=\\s*64.*NOT GLOB.*lower"),
        Check("backend_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(backend_code\\s*=\\s*'ffmpeg-region'\\)"),
        Check("audio_mode_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(audio_mode_code\\s*=\\s*'none'\\)"),
        Check("duration_ms\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(duration_ms\\s+BETWEEN\\s+1\\s+AND\\s+600000\\)"),
        Check("countdown_seconds\\s+INTEGER\\s+NOT NULL\\s+CHECK\\s*\\(countdown_seconds\\s+BETWEEN\\s+0\\s+AND\\s+10\\)"),
        Check("output_directory\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(output_directory\\)\\)\\s*>\\s*0.*length\\(output_directory\\)\\s*<=\\s*32767"),
        Check("filename_prefix\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(length\\(trim\\(filename_prefix\\)\\)\\s*>\\s*0.*length\\(filename_prefix\\)\\s*<=\\s*64"),
        Check("filename_template\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(filename_template\\s*=\\s*filename_prefix\\s+\\|\\|\\s*'-\\{scheduled_utc\\}-\\{occurrence_id\\}\\.mp4'\\)"),
        Check("output_conflict_policy_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(output_conflict_policy_code\\s*=\\s*'fail_if_exists'\\)"),
        Check("wake_policy_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(wake_policy_code\\s*=\\s*'natural_wake_only'\\)"),
        Check("desktop_requirement_code\\s+TEXT\\s+NOT NULL\\s+CHECK\\s*\\(desktop_requirement_code\\s*=\\s*'interactive_desktop_required'\\)"),
        Check("CHECK\\s*\\(region_x\\s*<=\\s*display_bounds_width\\s*-\\s*region_width\\)"),
        Check("CHECK\\s*\\(region_y\\s*<=\\s*display_bounds_height\\s*-\\s*region_height\\)"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        Trigger("trg_recurring_fixed_region_profile_versions_immutable_update", "UPDATE"),
        Trigger("trg_recurring_fixed_region_profile_versions_immutable_delete", "DELETE"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE recurring_fixed_region_profile_versions (
            profile_id TEXT NOT NULL CHECK (length(trim(profile_id)) > 0 AND length(profile_id) <= 128 AND profile_id = trim(profile_id) AND instr(profile_id, char(47)) = 0 AND instr(profile_id, char(92)) = 0 AND instr(profile_id, char(58)) = 0),
            profile_version INTEGER NOT NULL CHECK (profile_version > 0),
            profile_digest TEXT NOT NULL COLLATE BINARY CHECK (length(profile_digest) = length('recurring-fixed-region-profile/v1:') + 64 AND substr(profile_digest, 1, length('recurring-fixed-region-profile/v1:')) = 'recurring-fixed-region-profile/v1:' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) = lower(substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1))),
            created_at_utc INTEGER NOT NULL CHECK (created_at_utc >= 0),
            target_type_code TEXT NOT NULL CHECK (target_type_code = 'fixed_region'),
            rebind_policy_code TEXT NOT NULL CHECK (rebind_policy_code = 'exact_match_only'),
            capture_semantics_code TEXT NOT NULL CHECK (capture_semantics_code = 'desktop_region'),
            coordinate_space_code TEXT NOT NULL CHECK (coordinate_space_code = 'physical_virtual_screen'),
            display_identity_status_code TEXT NOT NULL CHECK (display_identity_status_code = 'resolved'),
            stable_display_fingerprint TEXT NOT NULL CHECK (length(trim(stable_display_fingerprint)) > 0 AND length(stable_display_fingerprint) <= 256 AND stable_display_fingerprint = trim(stable_display_fingerprint) AND instr(stable_display_fingerprint, char(47)) = 0 AND instr(stable_display_fingerprint, char(92)) = 0),
            display_bounds_x INTEGER NOT NULL CHECK (display_bounds_x BETWEEN -2147483648 AND 2147483647),
            display_bounds_y INTEGER NOT NULL CHECK (display_bounds_y BETWEEN -2147483648 AND 2147483647),
            display_bounds_width INTEGER NOT NULL CHECK (display_bounds_width BETWEEN 1 AND 2147483647),
            display_bounds_height INTEGER NOT NULL CHECK (display_bounds_height BETWEEN 1 AND 2147483647),
            region_x INTEGER NOT NULL CHECK (region_x BETWEEN 0 AND 2147483647),
            region_y INTEGER NOT NULL CHECK (region_y BETWEEN 0 AND 2147483647),
            region_width INTEGER NOT NULL CHECK (region_width BETWEEN 1 AND 2147483647),
            region_height INTEGER NOT NULL CHECK (region_height BETWEEN 1 AND 2147483647),
            dpi_x INTEGER NOT NULL CHECK (dpi_x > 0 AND dpi_x <= 2147483647),
            dpi_y INTEGER NOT NULL CHECK (dpi_y > 0 AND dpi_y <= 2147483647),
            physical_width INTEGER NOT NULL CHECK (physical_width > 0 AND physical_width <= 2147483647 AND physical_width = display_bounds_width),
            physical_height INTEGER NOT NULL CHECK (physical_height > 0 AND physical_height <= 2147483647 AND physical_height = display_bounds_height),
            orientation_code TEXT NOT NULL CHECK (orientation_code IN ('landscape', 'portrait', 'landscape_flipped', 'portrait_flipped')),
            topology_digest TEXT NOT NULL CHECK (length(topology_digest) = 64 AND substr(topology_digest, 1) NOT GLOB '*[^0-9a-f]*' AND topology_digest = lower(topology_digest)),
            backend_code TEXT NOT NULL CHECK (backend_code = 'ffmpeg-region'),
            audio_mode_code TEXT NOT NULL CHECK (audio_mode_code = 'none'),
            duration_ms INTEGER NOT NULL CHECK (duration_ms BETWEEN 1 AND 600000),
            countdown_seconds INTEGER NOT NULL CHECK (countdown_seconds BETWEEN 0 AND 10),
            output_directory TEXT NOT NULL CHECK (length(trim(output_directory)) > 0 AND length(output_directory) <= 32767 AND output_directory = trim(output_directory) AND instr(output_directory, char(0)) = 0 AND instr(output_directory, char(9)) = 0 AND instr(output_directory, char(10)) = 0 AND instr(output_directory, char(13)) = 0),
            filename_prefix TEXT NOT NULL CHECK (length(trim(filename_prefix)) > 0 AND length(filename_prefix) <= 64 AND filename_prefix = trim(filename_prefix) AND instr(filename_prefix, char(47)) = 0 AND instr(filename_prefix, char(92)) = 0 AND instr(filename_prefix, '..') = 0 AND substr(filename_prefix, -1) NOT IN ('.', ' ')),
            filename_template TEXT NOT NULL CHECK (filename_template = filename_prefix || '-{scheduled_utc}-{occurrence_id}.mp4'),
            output_conflict_policy_code TEXT NOT NULL CHECK (output_conflict_policy_code = 'fail_if_exists'),
            wake_policy_code TEXT NOT NULL CHECK (wake_policy_code = 'natural_wake_only'),
            desktop_requirement_code TEXT NOT NULL CHECK (desktop_requirement_code = 'interactive_desktop_required'),
            PRIMARY KEY (profile_id, profile_version),
            UNIQUE (profile_id, profile_version, profile_digest),
            UNIQUE (profile_digest),
            CHECK (region_x <= display_bounds_width - region_width),
            CHECK (region_y <= display_bounds_height - region_height)
        );

        CREATE INDEX idx_recurring_fixed_region_profile_versions_profile_version_desc
            ON recurring_fixed_region_profile_versions(profile_id, profile_version DESC);

        CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_update
            BEFORE UPDATE ON recurring_fixed_region_profile_versions
            BEGIN
                SELECT RAISE(ABORT, 'recurring_fixed_region_profile_versions_are_immutable');
            END;

        CREATE TRIGGER trg_recurring_fixed_region_profile_versions_immutable_delete
            BEFORE DELETE ON recurring_fixed_region_profile_versions
            BEGIN
                SELECT RAISE(ABORT, 'recurring_fixed_region_profile_versions_are_immutable');
            END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("recurring_fixed_region_profile_versions", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition Trigger(string name, string operation) =>
        new(
            name,
            "recurring_fixed_region_profile_versions",
            new[]
            {
                new Regex($"CREATE\\s+TRIGGER\\s+{name}\\s+BEFORE\\s+{operation}\\s+ON\\s+recurring_fixed_region_profile_versions", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                new Regex("BEGIN.*SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_fixed_region_profile_versions_are_immutable'\\s*\\).*END", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled),
            });

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixed schema v9 definition checksum changed.");
        }

        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}

internal static class SqliteSchemaV10
{
    public const string MigrationName = "schema_v10_recurring_plan_profile_bindings";
    public const string MigrationChecksum = "3ed33a8d8176dc56b35fa035f45a5bc2dd6ef95c70979065b73a399adee4e727";

    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } = Array.AsReadOnly(new[]
    {
        CreateMigration(10, MigrationName),
    });

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } = Array.AsReadOnly(new[]
    {
        new SqliteTableDefinition("recurring_plan_profile_bindings", new[]
        {
            new SqliteColumnDefinition("plan_id", "TEXT", true, 1),
            new SqliteColumnDefinition("profile_id", "TEXT", true, 0),
            new SqliteColumnDefinition("profile_version", "INTEGER", true, 0),
            new SqliteColumnDefinition("profile_digest", "TEXT", true, 0),
            new SqliteColumnDefinition("bound_at_utc", "INTEGER", true, 0),
        }),
    });

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } = Array.AsReadOnly(new[]
    {
        new SqliteIndexDefinition(
            "idx_recurring_plan_profile_bindings_profile_ref_plan_id",
            "recurring_plan_profile_bindings",
            false,
            new[] { "profile_id", "profile_version", "profile_digest", "plan_id" },
            DefinitionPattern: new Regex(
                "CREATE\\s+INDEX.*ON\\s+recurring_plan_profile_bindings\\s*\\(\\s*profile_id\\s*,\\s*profile_version\\s*,\\s*profile_digest\\s*,\\s*plan_id\\s*\\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled)),
    });

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } = Array.Empty<SqliteUniqueConstraintDefinition>();

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } = Array.AsReadOnly(new[]
    {
        new SqliteForeignKeyDefinition("recurring_plan_profile_bindings", new[]
        {
            new SqliteForeignKeyInfo("plans", "NO ACTION", "RESTRICT", new[] { "plan_id" }, new[] { "id" }),
            new SqliteForeignKeyInfo(
                "recurring_fixed_region_profile_versions",
                "NO ACTION",
                "RESTRICT",
                new[] { "profile_id", "profile_version", "profile_digest" },
                new[] { "profile_id", "profile_version", "profile_digest" }),
        }, RequireCompatibleCollation: true),
    });

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } = Array.Empty<SqliteDeferredForeignKeyDefinition>();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints { get; } = Array.AsReadOnly(new[]
    {
        Check("plan_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(plan_id,.*\\)\\)\\s*>\\s*0"),
        Check("profile_id\\s+TEXT\\s+NOT NULL.*CHECK\\s*\\(length\\(trim\\(profile_id,.*\\)\\)\\s*>\\s*0"),
        Check("profile_version\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(profile_version\\s*>\\s*0\\)"),
        Check("profile_digest\\s+TEXT\\s+NOT NULL\\s+COLLATE\\s+BINARY.*CHECK\\s*\\(length\\(profile_digest\\).*recurring-fixed-region-profile/v1:"),
        Check("bound_at_utc\\s+INTEGER\\s+NOT NULL.*CHECK\\s*\\(bound_at_utc\\s*>=\\s*0\\)"),
    });

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } = Array.AsReadOnly(new[]
    {
        InsertGateTrigger(
            "trg_recurring_plan_profile_bindings_periodic_only",
            "is_one_time\\s*=\\s*1",
            "recurring_plan_profile_bindings_require_periodic_plan"),
        InsertGateTrigger(
            "trg_recurring_plan_profile_bindings_draft_only",
            "status_code\\s*<>\\s*'draft'",
            "recurring_plan_profile_bindings_require_draft_plan"),
        ImmutableTrigger("trg_recurring_plan_profile_bindings_immutable_update", "UPDATE"),
        ImmutableTrigger("trg_recurring_plan_profile_bindings_immutable_delete", "DELETE"),
    });

    private const string SchemaDefinition = """
        CREATE TABLE recurring_plan_profile_bindings (
            plan_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(plan_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0),
            profile_id TEXT NOT NULL COLLATE BINARY CHECK (length(trim(profile_id, char(9) || char(10) || char(11) || char(12) || char(13) || char(32))) > 0 AND length(profile_id) <= 128 AND profile_id = trim(profile_id) AND instr(profile_id, char(47)) = 0 AND instr(profile_id, char(92)) = 0 AND instr(profile_id, char(58)) = 0),
            profile_version INTEGER NOT NULL CHECK (profile_version > 0),
            profile_digest TEXT NOT NULL COLLATE BINARY CHECK (length(profile_digest) = length('recurring-fixed-region-profile/v1:') + 64 AND substr(profile_digest, 1, length('recurring-fixed-region-profile/v1:')) = 'recurring-fixed-region-profile/v1:' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) NOT GLOB '*[^0-9a-f]*' AND substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1) = lower(substr(profile_digest, length('recurring-fixed-region-profile/v1:') + 1))),
            bound_at_utc INTEGER NOT NULL CHECK (bound_at_utc >= 0),
            PRIMARY KEY (plan_id),
            FOREIGN KEY (plan_id) REFERENCES plans(id) ON DELETE RESTRICT,
            FOREIGN KEY (profile_id, profile_version, profile_digest) REFERENCES recurring_fixed_region_profile_versions(profile_id, profile_version, profile_digest) ON DELETE RESTRICT
        );

        CREATE INDEX idx_recurring_plan_profile_bindings_profile_ref_plan_id
            ON recurring_plan_profile_bindings(profile_id, profile_version, profile_digest, plan_id);

        CREATE TRIGGER trg_recurring_plan_profile_bindings_periodic_only
            BEFORE INSERT ON recurring_plan_profile_bindings
            BEGIN
                SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_require_periodic_plan')
                WHERE EXISTS (SELECT 1 FROM plans WHERE id = NEW.plan_id AND is_one_time = 1);
            END;

        CREATE TRIGGER trg_recurring_plan_profile_bindings_draft_only
            BEFORE INSERT ON recurring_plan_profile_bindings
            BEGIN
                SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_require_draft_plan')
                WHERE EXISTS (SELECT 1 FROM plans WHERE id = NEW.plan_id AND status_code <> 'draft');
            END;

        CREATE TRIGGER trg_recurring_plan_profile_bindings_immutable_update
            BEFORE UPDATE ON recurring_plan_profile_bindings
            BEGIN
                SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_are_immutable');
            END;

        CREATE TRIGGER trg_recurring_plan_profile_bindings_immutable_delete
            BEFORE DELETE ON recurring_plan_profile_bindings
            BEGIN
                SELECT RAISE(ABORT, 'recurring_plan_profile_bindings_are_immutable');
            END;
        """;

    private static SqliteCheckConstraintDefinition Check(string pattern) =>
        new("recurring_plan_profile_bindings", new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled));

    private static SqliteTriggerDefinition InsertGateTrigger(string name, string predicate, string message) =>
        new(
            name,
            "recurring_plan_profile_bindings",
            Array.Empty<Regex>(),
            new Regex(
                $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+INSERT\\s+ON\\s+recurring_plan_profile_bindings\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'{Regex.Escape(message)}'\\s*\\)\\s+WHERE\\s+EXISTS\\s*\\(\\s*SELECT\\s+1\\s+FROM\\s+plans\\s+WHERE\\s+id\\s*=\\s*NEW\\.plan_id\\s+AND\\s+{predicate}\\s*\\)\\s*;\\s*END\\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteTriggerDefinition ImmutableTrigger(string name, string operation) =>
        new(
            name,
            "recurring_plan_profile_bindings",
            Array.Empty<Regex>(),
            new Regex(
                $"^CREATE\\s+TRIGGER\\s+{Regex.Escape(name)}\\s+BEFORE\\s+{Regex.Escape(operation)}\\s+ON\\s+recurring_plan_profile_bindings\\s+BEGIN\\s+SELECT\\s+RAISE\\s*\\(\\s*ABORT\\s*,\\s*'recurring_plan_profile_bindings_are_immutable'\\s*\\)\\s*;\\s*END\\s*;?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    private static SqliteMigrationDefinition CreateMigration(int version, string name)
    {
        var canonicalDefinition = SqliteSchemaV1.CanonicalizeDefinition(SchemaDefinition);
        var checksum = SqliteSchemaV1.ComputeChecksum(canonicalDefinition);
        if (!string.Equals(checksum, MigrationChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The fixed schema v10 definition checksum changed.");
        }

        return new SqliteMigrationDefinition(version, name, canonicalDefinition, MigrationChecksum);
    }
}

internal static class SqliteSchemaCatalog
{
    public static IReadOnlyList<SqliteMigrationDefinition> Migrations { get; } =
        SqliteSchemaV1.Migrations.Concat(SqliteSchemaV2.Migrations).Concat(SqliteSchemaV3.Migrations).Concat(SqliteSchemaV4.Migrations).Concat(SqliteSchemaV5.Migrations).Concat(SqliteSchemaV6.Migrations).Concat(SqliteSchemaV7.Migrations).Concat(SqliteSchemaV8.Migrations).Concat(SqliteSchemaV9.Migrations).Concat(SqliteSchemaV10.Migrations).Concat(SqliteSchemaV11.Migrations).Concat(SqliteSchemaV12.Migrations).Concat(SqliteSchemaV13.Migrations).ToArray();

    public static IReadOnlyList<SqliteTableDefinition> Tables { get; } =
        SqliteSchemaV1.Tables.Concat(SqliteSchemaV2.Tables).Concat(SqliteSchemaV3.Tables).Concat(SqliteSchemaV5.Tables).Concat(SqliteSchemaV7.Tables).Concat(SqliteSchemaV8.Tables).Concat(SqliteSchemaV9.Tables).Concat(SqliteSchemaV10.Tables).Concat(SqliteSchemaV11.Tables).Concat(SqliteSchemaV12.Tables).Concat(SqliteSchemaV13.Tables).ToArray();

    public static IReadOnlyList<SqliteIndexDefinition> Indexes { get; } =
        SqliteSchemaV1.Indexes.Concat(SqliteSchemaV2.Indexes).Concat(SqliteSchemaV3.Indexes).Concat(SqliteSchemaV5.Indexes).Concat(SqliteSchemaV7.Indexes).Concat(SqliteSchemaV8.Indexes).Concat(SqliteSchemaV9.Indexes).Concat(SqliteSchemaV10.Indexes).Concat(SqliteSchemaV11.Indexes).Concat(SqliteSchemaV12.Indexes).Concat(SqliteSchemaV13.Indexes).ToArray();

    public static IReadOnlyList<SqliteUniqueConstraintDefinition> UniqueConstraints { get; } =
        SqliteSchemaV1.UniqueConstraints.Concat(SqliteSchemaV2.UniqueConstraints).Concat(SqliteSchemaV3.UniqueConstraints).Concat(SqliteSchemaV5.UniqueConstraints).Concat(SqliteSchemaV7.UniqueConstraints).Concat(SqliteSchemaV8.UniqueConstraints).Concat(SqliteSchemaV9.UniqueConstraints).Concat(SqliteSchemaV10.UniqueConstraints).Concat(SqliteSchemaV11.UniqueConstraints).Concat(SqliteSchemaV12.UniqueConstraints).Concat(SqliteSchemaV13.UniqueConstraints).ToArray();

    public static IReadOnlyList<SqliteForeignKeyDefinition> ForeignKeys { get; } =
        SqliteSchemaV1.ForeignKeys.Concat(SqliteSchemaV2.ForeignKeys).Concat(SqliteSchemaV3.ForeignKeys).Concat(SqliteSchemaV5.ForeignKeys).Concat(SqliteSchemaV7.ForeignKeys).Concat(SqliteSchemaV8.ForeignKeys).Concat(SqliteSchemaV9.ForeignKeys).Concat(SqliteSchemaV10.ForeignKeys).Concat(SqliteSchemaV11.ForeignKeys).Concat(SqliteSchemaV12.ForeignKeys).Concat(SqliteSchemaV13.ForeignKeys).ToArray();

    public static IReadOnlyList<SqliteDeferredForeignKeyDefinition> DeferredForeignKeys { get; } =
        SqliteSchemaV1.DeferredForeignKeys.Concat(SqliteSchemaV2.DeferredForeignKeys).Concat(SqliteSchemaV3.DeferredForeignKeys).Concat(SqliteSchemaV5.DeferredForeignKeys).Concat(SqliteSchemaV7.DeferredForeignKeys).Concat(SqliteSchemaV8.DeferredForeignKeys).Concat(SqliteSchemaV9.DeferredForeignKeys).Concat(SqliteSchemaV10.DeferredForeignKeys).Concat(SqliteSchemaV11.DeferredForeignKeys).Concat(SqliteSchemaV12.DeferredForeignKeys).Concat(SqliteSchemaV13.DeferredForeignKeys).ToArray();

    public static IReadOnlyList<SqliteCheckConstraintDefinition> CheckConstraints =>
        SqliteSchemaV2.CheckConstraints.Concat(SqliteSchemaV3.CheckConstraints).Concat(SqliteSchemaV4.CheckConstraints).Concat(SqliteSchemaV5.CheckConstraints).Concat(SqliteSchemaV7.CheckConstraints).Concat(SqliteSchemaV8.CheckConstraints).Concat(SqliteSchemaV9.CheckConstraints).Concat(SqliteSchemaV10.CheckConstraints).Concat(SqliteSchemaV11.CheckConstraints).Concat(SqliteSchemaV12.CheckConstraints).Concat(SqliteSchemaV13.CheckConstraints).ToArray();

    public static IReadOnlyList<SqliteTriggerDefinition> Triggers { get; } =
        SqliteSchemaV9.Triggers.Concat(SqliteSchemaV10.Triggers).Concat(SqliteSchemaV11.Triggers).Concat(SqliteSchemaV12.Triggers).Concat(SqliteSchemaV13.Triggers).ToArray();
}
