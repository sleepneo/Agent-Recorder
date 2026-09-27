using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

internal static class SqliteRecordingRunOutputEvidence
{
    internal static string InsertVerifiedWithinTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string occurrenceId,
        string outputPath,
        DateTimeOffset recordedAtUtc)
    {
        if (recordedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Output evidence time must be UTC.", nameof(recordedAtUtc));

        var normalizedPath = Path.GetFullPath(outputPath);
        if (string.IsNullOrWhiteSpace(normalizedPath) || !Path.IsPathFullyQualified(normalizedPath))
            throw new ArgumentException("A verified output path must be fully qualified.", nameof(outputPath));

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recording_run_output_evidence
                (run_id, occurrence_id, evidence_kind_code, output_path, recorded_at_utc)
            VALUES ($run_id, $occurrence_id, 'verified_output_path', $output_path, $recorded_at_utc);
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$occurrence_id", occurrenceId);
        command.Parameters.AddWithValue("$output_path", normalizedPath);
        command.Parameters.AddWithValue("$recorded_at_utc", recordedAtUtc.UtcDateTime.Ticks);
        command.ExecuteNonQuery();
        return normalizedPath;
    }
}
