using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal sealed record LocalRetentionFacts(string RecordingId, PublicationIdentityAnchor Anchor,
    DateTimeOffset CompletedAtUtc, DateTimeOffset Cutoff);

// Exact, bounded, existing-only read. Nothing here reserves a path or samples a
// new physical identity. All provider objects retire before returning facts.
internal sealed class LocalRetentionFactsReader(SqliteOperationalStore store, SqliteOrdinaryRecordingHistoryStore writer,
    string sid, string binding, string root, TimeProvider clock, Func<string, bool>? exists = null)
{
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    internal bool Retired
    {
        get { try { return _connection is null || _connection.State == System.Data.ConnectionState.Closed && _connection.Handle is null && _transaction?.Connection is null; } catch { return false; } }
    }
    internal LocalRetentionFacts? Read(string id, CancellationToken cancel)
    {
        var started = clock.GetTimestamp(); var cutoff = clock.GetUtcNow().ToUniversalTime();
        void Check() { cancel.ThrowIfCancellationRequested(); if (clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(3)) throw new TimeoutException(); }
        if (!UnifiedHistoryQueryService.ValidIdentity(sid) || !UnifiedHistoryQueryService.ValidIdentity(binding) ||
            !UnifiedHistoryQueryService.ValidId(id) || id.IndexOfAny(new[] { '/', '\\' }) >= 0) return null;
        Check(); writer.FlushBeforeUnifiedRead(TimeSpan.FromSeconds(3)); Check();
        LocalRetentionFacts? facts;
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 1 }.ToString()))
        {
            _connection = c;
            c.Open(); SqliteOperationalStore.ConfigureRetentionGuardConnection(c); Check();
            using var t = c.BeginTransaction(deferred: true);
            _transaction = t;
            SqliteOperationalStore.ValidateRetentionGuardStore(c, t); Check();
            var selected = RetentionReferenceGuard.ReadExactCandidate(new(store, writer), c, t, id);
            if (selected is null) return null;
            var (row, entry) = selected.Value;
            // Retention can preserve an invalid own role as a protected
            // relationship. Local approval must not sign damaged own facts.
            SqliteOrdinaryRecordingHistoryStore.ValidateSnapshot(row);
            RetentionReferenceGuard.ValidateExactText(c, t, id, "ordinary_recording_bundles",
                new[] { "recording_id", "media_output_path", "operation_id", "status_code", "bundle_path", "error_code" });
            RetentionReferenceGuard.ValidateExactText(c, t, id, "ordinary_publication_anchors", new[] { "recording_id", "output_path", "source" });
            var rows = new[] { row };
            var anchor = PublicationAnchorStorage.Read(c, t, rows)[id];
            var bundle = OrdinaryRecordingBundleStorage.Read(c, t, rows)[id];
            if (row.RecordingId != id || row.Status != "completed" || !entry.OutputPathRecorded ||
                row.CompletedAtUtc is not { } completed || completed >= cutoff || anchor is null ||
                !string.Equals(anchor.Path, row.OutputPath, StringComparison.Ordinal) || bundle?.Status != "not_applicable" ||
                !OriginalFileHoldService.ValidScopeInput(root, anchor)) return null;
            facts = new(id, anchor, completed, cutoff); Check();
        }
        Check();
        // Existence only, no identity issuance/content/thumbnail. The actual
        // protected object is rechecked by Task357 after human approval.
        var present = (exists ?? File.Exists)(facts.Anchor.Path);
        Check(); // Existence is only a hint, and slow IO cannot issue late facts.
        return present ? facts : null;
    }
}
