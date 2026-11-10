using System.Diagnostics;
using AgentRecorder.Capture;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record ManagedVideoReconciliationSample(
    string? RootState, int MatchedFiles, int FilesWithoutMatchingRegistration, int MissingRegistrations,
    int UncertainCandidates, DateTimeOffset SampledAtUtc, string[] ReasonCodes, string? UnavailableReason = null);

internal sealed class ManagedVideoReconciliation(SqliteOperationalStore store, string trustedDataDirectory)
{
    internal const int DatabaseMilliseconds = 1500;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private readonly string _videosRoot = Path.Combine(Path.GetFullPath(trustedDataDirectory), "Videos");
    private readonly object _gate = new();
    private TaskCompletionSource? _active;

    internal Action? AfterListingForTests { get; init; }
    internal Func<CancellationToken, Task>? DeadlineForTests { get; init; }

    internal Task WaitForIdleAsync()
    {
        lock (_gate) return _active?.Task ?? Task.CompletedTask;
    }

    internal async Task<ManagedVideoReconciliationSample> ReadAsync(CancellationToken cancellation = default)
    {
        var started = Stopwatch.GetTimestamp();
        TaskCompletionSource retired;
        lock (_gate)
        {
            if (cancellation.IsCancellationRequested) return Unavailable("cancelled");
            if (_active?.Task.IsCompleted == false) return Unavailable("busy");
            retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _active = retired;
        }
        return await ArtifactMetadataWorker.ReadAsync(token => Read(started, token), Unavailable,
            DeadlineForTests ?? (token => Task.Delay(Timeout, token)), null, cancellation, retired).ConfigureAwait(false)
            ?? Unavailable("failed");
    }

    private static ManagedVideoReconciliationSample Unavailable(string reason) =>
        new(null, 0, 0, 0, 0, default, [], reason);

    private ManagedVideoReconciliationSample Read(long started, CancellationToken token)
    {
        void CheckTotal()
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= Timeout) throw new BudgetExceeded("timeout");
        }

        try
        {
            CheckTotal();
            if (!OriginalFileHoldService.ValidScopePath(_videosRoot, Path.Combine(_videosRoot, "a.mp4")))
                return Unavailable("root_unavailable");
            var scanStarted = Stopwatch.GetTimestamp();
            void CheckScan()
            {
                CheckTotal();
                if (Stopwatch.GetElapsedTime(scanStarted).TotalMilliseconds >= WindowsManagedVideoInventory.ScanMilliseconds)
                    throw new BudgetExceeded("scan_timeout");
            }
            var directory = WindowsManagedVideoInventory.Observe(_videosRoot, CheckScan, AfterListingForTests);
            CheckScan();
            if (directory.FailureReason is { } reason) return Unavailable(reason);
            var databaseStarted = Stopwatch.GetTimestamp();
            void CheckDatabase()
            {
                CheckTotal();
                if (Stopwatch.GetElapsedTime(databaseStarted).TotalMilliseconds >= DatabaseMilliseconds)
                    throw new BudgetExceeded("database_timeout");
            }
            var rows = ReadRegistrations(CheckDatabase, directory.AlternateComponents);
            CheckDatabase();
            var result = Compare(rows, directory, DateTimeOffset.UtcNow, CheckTotal);
            CheckTotal();
            return result;
        }
        catch (OperationCanceledException) { return Unavailable("cancelled"); }
        catch (BudgetExceeded ex) { return Unavailable(ex.Reason); }
        catch (ManagedVideoUsage.UsageLimitException ex) { return Unavailable(ex.Reason); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        { return Unavailable("data_invalid"); }
        catch (SqliteOperationalStoreException ex) when (ex.Code == "sqlite_corrupt") { return Unavailable("data_invalid"); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) { return Unavailable("data_invalid"); }
        catch (Exception) { return Unavailable("failed"); }
    }

    private List<ManagedVideoUsage.Registration> ReadRegistrations(Action check, string?[]? alternateComponents)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private,
            Pooling = false, DefaultTimeout = 1
        }.ToString());
        connection.Open();
        check();
        SqliteOperationalStore.ConfigureRetentionGuardConnection(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        SqliteOperationalStore.ValidateRetentionGuardStore(connection, transaction);
        SqliteSchemaV30.Validate(connection, transaction);
        check();
        var rows = new List<ManagedVideoUsage.Registration>();
        Func<string, bool>? matchesAlias = alternateComponents?.Any(component => component is not null) == true
            ? path => IsAlternatePath(_videosRoot, alternateComponents, path) : null;
        ManagedVideoUsage.ReadRegistrations(connection, transaction, _videosRoot, check, rows.Add, matchesAlias);
        return rows;
    }

    internal static bool IsAlternatePath(string root, string?[] aliases, string path)
    {
        if (!WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(path) || !path.StartsWith(root[..3], StringComparison.OrdinalIgnoreCase))
            return false;
        var canonical = root[3..].Split('\\');
        var parts = path[3..].Split('\\');
        if (parts.Length != canonical.Length + 1 || aliases.Length != canonical.Length ||
            !OriginalFileHoldService.ValidScopePath(root, Path.Combine(root, parts[^1]))) return false;
        bool usedAlias = false;
        for (int i = 0; i < canonical.Length; i++)
        {
            if (Names.Equals(parts[i], canonical[i])) continue;
            if (!Names.Equals(parts[i], aliases[i])) return false;
            usedAlias = true;
        }
        return usedAlias;
    }

    internal static ManagedVideoReconciliationSample Compare(IReadOnlyList<ManagedVideoUsage.Registration> rows,
        ManagedVideoDirectoryObservation directory, DateTimeOffset sampledAt, Action check)
    {
        var registrations = rows.ToLookup(row => Path.GetFileName(row.Path), Names);
        var entries = directory.Entries.ToLookup(entry => entry.Name, Names);
        var aliasConflicts = new HashSet<string>(Names);
        var namesByIdentity = new Dictionary<ObservedFileIdentity, HashSet<string>>();
        foreach (var row in rows)
        {
            check();
            if (row.Anchor is { } anchor) AddIdentity(anchor.Identity, Path.GetFileName(row.Path));
        }
        foreach (var entry in directory.Entries)
        {
            check();
            if (entry.ListedIdentity is { } identity) AddIdentity(identity, entry.Name);
            if (entry.ShortName.Length != 0 && !Names.Equals(entry.ShortName, entry.Name) &&
                (registrations.Contains(entry.ShortName) || entries.Contains(entry.ShortName)))
            {
                aliasConflicts.Add(entry.Name);
                aliasConflicts.Add(entry.ShortName);
            }
        }

        var candidates = registrations.Select(group => group.Key)
            .Union(directory.Entries.Where(entry => entry.FileCandidate).Select(entry => entry.Name), Names);
        int matched = 0, unregistered = 0, missing = 0, uncertain = 0;
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in candidates)
        {
            check();
            var saved = registrations[name].ToArray();
            var found = entries[name].ToArray();
            var anchor = saved.FirstOrDefault()?.Anchor;
            var current = found.FirstOrDefault();

            string? reason = aliasConflicts.Contains(name) || found.Length > 1 || saved.Any(row => row.ThroughRootAlias) ? "path_or_identity_conflict" :
                current?.Reason;
            if (reason is null && saved.Any(row => row.Anchor is null))
                reason = "publication_evidence_missing";
            if (reason is null && anchor is not null && saved.Any(row => !Compatible(anchor, row.Anchor!)))
                reason = "publication_identity_conflict";
            if (reason is null && (OtherName(anchor?.Identity) || OtherName(current?.ListedIdentity)))
                reason = "path_or_identity_conflict";
            if (reason is null && current is not null && current.VerifiedLength is null)
                reason = "entry_unverified";
            if (reason is null && anchor is not null && current is not null &&
                (current.ListedIdentity != anchor.Identity || current.VerifiedLength != anchor.Length))
                reason = "path_or_identity_conflict";

            if (reason is not null)
            {
                uncertain++;
                reasons.Add(reason);
            }
            else if (current is null) missing++;
            else if (anchor is null) unregistered++;
            else matched++;
        }
        return new(directory.Absent ? "absent" : "present", matched, unregistered, missing, uncertain, sampledAt, reasons.ToArray());

        void AddIdentity(ObservedFileIdentity identity, string name)
        {
            if (!namesByIdentity.TryGetValue(identity, out var names)) namesByIdentity[identity] = names = new(Names);
            names.Add(name);
        }
        bool OtherName(ObservedFileIdentity? identity) => identity is not null && namesByIdentity[identity].Count > 1;
    }

    private static bool Compatible(PublicationIdentityAnchor left, PublicationIdentityAnchor right) =>
        left.Identity == right.Identity && left.Length == right.Length && Names.Equals(left.Path, right.Path);

    private sealed class BudgetExceeded(string reason) : Exception
    {
        internal string Reason { get; } = reason;
    }
}
