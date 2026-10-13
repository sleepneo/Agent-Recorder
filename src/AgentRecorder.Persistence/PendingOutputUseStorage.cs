using System.Globalization;
using System.Text;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using Microsoft.Data.Sqlite;

namespace AgentRecorder.Persistence;

// A bounded configuration range, not recording authorization or a reference graph.
internal static class PendingOutputUseStorage
{
    internal const int MaximumRelatedUses = 500;
    internal static Dictionary<string, PendingOutputUseState> Read(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<OrdinaryRecordingHistorySnapshot> rows,
        Action<SqliteConnection,SqliteTransaction,string,IReadOnlyList<KeyValuePair<string,object>>>? observe)
    {
        var targets = rows.Select(r => UnifiedHistoryQueryService.SplitFrozenTarget(r.OutputPath)).ToArray();
        var result = rows.ToDictionary(r => r.RecordingId, _ => PendingOutputUseState.None, StringComparer.Ordinal);
        var directories = targets.SelectMany(t => new[] { t.WithoutTail, t.WithTail }).Where(d => d is not null)
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (directories.Length == 0) return result; // Existing frozen-path indeterminate handling owns unsplittable candidates.
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        // SQLite length(TEXT) stops at NUL. Do not rely on a driver's GetString
        // preserving damaged suffixes. Check the complete
        // value before materialization: a UTF-8 byte ceiling cannot exclude valid text
        // within the existing character ceiling (at most four bytes per scalar).
        // Keep rejected rows in the batch, associated by the trusted requested directory.
        static string Text(string column,int maximum) => "CASE WHEN typeof(uses."+column+")='text' AND length(CAST(uses."+column+" AS BLOB))<="+(4*maximum)+" AND instr(uses."+column+",char(0))=0 AND length(uses."+column+")<="+maximum+" THEN uses."+column+" ELSE NULL END";
        // Preserve old branch kinds: 0=fixed, 1=setup template, 2=Profile template.
        var paths = SqliteSchemaV38.Paths.Select((p,i) => (Kind:i, Path:p))
            .Concat(SqliteSchemaV39.Paths.Select(p => (Kind:0, Path:p)));
        var branches = paths.Select(source =>
        {
            var p = source.Path; var i = source.Kind;
            return "SELECT " + i + ",requested.directory," + Text(p.Columns[0],RetentionPreviewQueryService.MaxPathLength+1) + "," + Text(i == 0 ? "frozen_file_name" : i == 1 ? "recurring_filename_prefix" : "filename_prefix",i == 0 ? 255 : 128) +
            (i == 2 ? ","+Text("filename_template",200) : ",NULL") + " FROM requested CROSS JOIN " + p.Table + " uses INDEXED BY " + p.Index +
            " WHERE uses." + p.Columns[0] + " COLLATE " + SqliteOutputPathCollation.Name + "=requested.directory";
        });
        command.CommandText = "WITH requested(directory) AS (VALUES " + string.Join(",", directories.Select((_,i) => "($dir"+i+")")) +
            ") SELECT * FROM (" + string.Join(" UNION ALL ", branches) + ") LIMIT 501;";
        for (var i=0;i<directories.Length;i++) command.Parameters.AddWithValue("$dir"+i,directories[i]);
        observe?.Invoke(connection,transaction,command.CommandText,command.Parameters.Cast<SqliteParameter>()
            .Select(p=>new KeyValuePair<string,object>(p.ParameterName,p.Value!)).ToArray());
        var uses = new List<(int Kind, string Directory, string? CompleteDirectory, string? Value, string? Template)>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            if (uses.Count == MaximumRelatedUses) throw new InvalidDataException("Pending output use limit exceeded.");
            uses.Add((reader.GetInt32(0),reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4)));
        }
        // Only the selected <=500 rows are classified, after the global limit check.
        foreach (var use in uses)
        {
            var validDirectory = use.CompleteDirectory is not null && ValidDirectory(use.CompleteDirectory);
            var valid = validDirectory && (use.Kind == 0 ? ValidFileName(use.Value) : ValidPrefix(use.Value) &&
                (use.Kind == 1 || string.Equals(use.Template, use.Value+"-{scheduled_utc}-{occurrence_id}.mp4", StringComparison.Ordinal)));
            for (var i=0;i<rows.Count;i++)
            {
                var target = targets[i];
                if (!string.Equals(use.Directory,target.WithoutTail,StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(use.Directory,target.WithTail,StringComparison.OrdinalIgnoreCase)) continue;
                var state = !valid ? PendingOutputUseState.Unknown : use.Kind == 0 ?
                    (string.Equals(use.Value,target.Name,StringComparison.OrdinalIgnoreCase)?PendingOutputUseState.SetupExact:PendingOutputUseState.None) :
                    (MatchesTemplate(target.Name,use.Value!)?PendingOutputUseState.Template:PendingOutputUseState.None);
                var old = result[rows[i].RecordingId];
                // An exact known purpose wins; uncertainty remains visible ahead of a template hit.
                if (state == PendingOutputUseState.SetupExact || old != PendingOutputUseState.SetupExact &&
                    (state == PendingOutputUseState.Unknown || old == PendingOutputUseState.None)) result[rows[i].RecordingId] = state;
            }
        }
        return result;
    }

    private static bool ValidDirectory(string value)
    {
        if (value.Length == 3) return char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';
        var withoutTail = value.EndsWith('\\') ? value[..^1] : value;
        return withoutTail.Length <= RetentionPreviewQueryService.MaxPathLength && WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(withoutTail);
    }
    private static bool ValidFileName(string? value) => value is { Length: >0 and <=255 } && value == value.Trim() &&
        value is not ("." or "..") && !value.EndsWith('.') && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    internal static bool ValidPrefix(string? value)
    {
        if (value is null || value.Length is <1 or >128 || value != value.Trim() || value.Contains("..",StringComparison.Ordinal)) return false;
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (++count > 64 || !(Rune.IsLetterOrDigit(rune) || rune.IsAscii && (rune.Value is '-' or '_' or ' '))) return false;
        }
        var stem = value.Split('-')[0];
        return !(stem.Equals("CON",StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN",StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX",StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL",StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM",StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT",StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
    }
    internal static bool MatchesTemplate(string? name, string prefix)
    {
        if (name is null || name.Length != prefix.Length+86 || !name.StartsWith(prefix+"-",StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase) || name[prefix.Length+17] != '-') return false;
        var timestamp = name.Substring(prefix.Length+1,16).ToUpperInvariant();
        if (!DateTime.TryParseExact(timestamp,"yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture,DateTimeStyles.None,out _)) return false;
        foreach (var c in name.AsSpan(prefix.Length+18,64))
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }
}
