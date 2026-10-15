using AgentRecorder.Core;
using AgentRecorder.Infrastructure;

namespace AgentRecorder.Persistence;

/// <summary>One history page, evaluated without directory enumeration. Metadata
/// is sampled only after lexical scope and bounded ancestor checks.</summary>
public sealed class RetentionPreviewQueryService
{
    public const int MaxPathLength = 1024;
    public const int MaxPathComponents = 32;
    public const int MaxAttributeChecksPerEntry = MaxPathComponents;
    private readonly Func<string, string, int, DateTimeOffset, string?, OrdinaryRetentionEvidencePage> _read;
    private readonly IReadOnlyPathMetadata _files;
    private readonly IReadOnlyFileMetadataProbe? _probe;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _videosRoot;
    private readonly IOrdinaryRuntimeOutputUseProvider? _runtimeOutputs;
    internal string VideosRootForTests => _videosRoot;

    public RetentionPreviewQueryService(UnifiedHistoryQueryService history, string hostDataDirectory,
        IOrdinaryRuntimeOutputUseProvider? runtimeOutputs)
        : this(history.GetOrdinaryEvidencePage, hostDataDirectory, ReadOnlyPathMetadata.Instance,
            WindowsReadOnlyFileMetadataProbe.Instance, () => DateTimeOffset.UtcNow, runtimeOutputs) { }

    internal RetentionPreviewQueryService(Func<string, string, int, DateTimeOffset, string?, OrdinaryRetentionEvidencePage> read,
        string hostDataDirectory, IReadOnlyPathMetadata files, IReadOnlyFileMetadataProbe? probe, Func<DateTimeOffset> now,
        IOrdinaryRuntimeOutputUseProvider? runtimeOutputs)
    {
        _read = read; _files = files; _probe = probe; _now = now; _runtimeOutputs = runtimeOutputs;
        // Fixed once by host composition; never consult output-settings or HTTP.
        _videosRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(hostDataDirectory, "Videos")));
    }

    public RetentionPreviewPage GetPage(string sid, string session, int limit, DateTimeOffset completedBefore, string? cursor = null)
    {
        if (limit is < 1 or > 100 || completedBefore.Offset != TimeSpan.Zero || completedBefore > _now())
            throw new UnifiedHistoryArgumentException();
        try
        {
            var page = _read(sid, session, limit, completedBefore, cursor);
            if (page.PageSize!=limit || page.Items.Count > limit || page.Filters != new UnifiedHistoryFilters("ordinary", CreatedBefore: completedBefore) ||
                page.Items.Any(e => e.Entry.Source != "ordinary" || e.Entry.EntryKind != "ordinary_video" ||
                    !UnifiedHistoryQueryService.ValidId(e.Entry.RecordingId) || e.Entry.EntryId!="ordinary:"+e.Entry.RecordingId ||
                    !OrdinaryRecordingBundleStorage.ValidSnapshot(e.Entry.Bundle, e.Entry.OutputPath, e.Entry.Status) ||
                    e.NestedReference is null || !Enum.IsDefined(e.NestedReference.Value) ||
                    e.SharedOutputReference is null || !Enum.IsDefined(e.SharedOutputReference.Value) ||
                    e.PlannedOutputReference is null || !Enum.IsDefined(e.PlannedOutputReference.Value) ||
                    !Enum.IsDefined(e.PendingOutputUse) ||
                    (e.PublicationAnchor is null ? e.PublicationIdentityReference is not null :
                        e.PublicationIdentityReference is null || !Enum.IsDefined(e.PublicationIdentityReference.Value)) ||
                    e.PublicationAnchor is { } anchor && (!anchor.Valid || e.Entry.Status != "completed" ||
                        e.Entry.CompletedAtUtc is null || anchor.Length != e.Entry.OutputSizeBytes ||
                        !string.Equals(anchor.Path, e.Entry.OutputPath, StringComparison.OrdinalIgnoreCase))) ||
                page.Items.Select(e=>e.Entry.EntryId).Distinct(StringComparer.Ordinal).Count()!=page.Items.Count)
                throw new InvalidDataException("Invalid ordinary evidence page.");
            // Resolve every eligible unknown before sampling any item. Invalid
            // non-candidates keep their earlier reason instead of failing on split.
            if (page.Items.Any(e => e.PlannedOutputReference == PlannedOutputPathReferenceState.Indeterminate &&
                e.NestedReference == OrdinaryNestedReferenceState.None && e.SharedOutputReference == SharedOutputPathReferenceState.None &&
                e.Entry.Status == "completed" && e.Entry.CompletedAtUtc is { } at && at < completedBefore &&
                e.Entry.OutputPathRecorded && e.Entry.OutputPath is { } path && ValidateScopedPath(path, out _) is null))
                throw new InvalidDataException("Retention planned path matching is unavailable.");
            if (_probe is null) throw new InvalidDataException("Retention file observation provider is unavailable.");
            // Validate the complete DB page first, then one complete runtime
            // answer, before sampling any item. These are separate time points.
            var paths = page.Items.Where(e => e.Entry.Status == "completed" &&
                e.Entry.CompletedAtUtc is { } at && at < completedBefore && e.Entry.OutputPathRecorded &&
                e.Entry.OutputPath is { } path && ValidateScopedPath(path, out _) is null)
                .Select(e => e.Entry.OutputPath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var occupied = ReadRuntimeSnapshot(paths);
            var items = page.Items.Select(e => Evaluate(e, completedBefore, occupied)).ToArray();
            var candidates = items.Where(e => e.AgeCandidate).ToArray();
            long bytes = 0; var samples = 0;
            foreach (var item in candidates)
                if (item.ObservedFileBytes is { } length) { bytes = checked(bytes + length); samples++; }
            return new(Array.AsReadOnly(items), page.NextCursor, limit, completedBefore, items.Length,
                candidates.Length, bytes, samples, candidates.Length - samples);
        }
        catch (UnifiedHistoryArgumentException) { throw; }
        catch (Exception e) { throw new RetentionPreviewUnavailableException(e); }
    }

    private HashSet<string> ReadRuntimeSnapshot(string[] paths)
    {
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (paths.Length == 0) return occupied;
        OrdinaryRuntimeOutputSnapshot? snapshot;
        try { snapshot = _runtimeOutputs?.ReadSnapshot(Array.AsReadOnly(paths)); }
        catch (Exception error)
        {
            // Provider faults are service unavailability even if their exception
            // type is also used by the user argument/cursor validation path.
            throw new InvalidDataException("Retention runtime output snapshot failed.", error);
        }
        if (snapshot is null || snapshot.Paths.Count != paths.Length || paths.Length > OrdinaryRuntimeOutputSnapshot.MaxCandidates)
            throw new InvalidDataException("Retention runtime output snapshot is unavailable.");
        var requested = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var use in snapshot.Paths)
        {
            if (use is null || use.Path is null || !requested.Contains(use.Path) || !seen.Add(use.Path))
                throw new InvalidDataException("Invalid retention runtime output snapshot.");
            if (use.InUse) occupied.Add(use.Path);
        }
        return occupied;
    }

    private RetentionPreviewEntry Evaluate(OrdinaryRetentionEvidence evidence, DateTimeOffset before, HashSet<string> occupied)
    {
        var item=evidence.Entry;
        RetentionPreviewEntry Result(string evaluation, string reason, bool age = false, string? path = null, long? observed = null)
            => new(item.EntryId, item.RecordingId, item.Status, item.CompletedAtUtc, evaluation, reason,
                age, path, path is null ? null : item.OutputSizeBytes, observed);
        if (item.Status != "completed") return Result("protected", "not_completed");
        if (item.CompletedAtUtc is null) return Result("protected", "completion_time_unknown");
        if (!item.OutputPathRecorded || item.OutputPath is null) return Result("protected", "output_evidence_missing");
        if (item.CompletedAtUtc >= before) return Result("skipped", "completion_not_before_cutoff");
        var path = item.OutputPath;
        var reason = ValidateScopedPath(path, out var checks);
        if (reason is not null) return Result("protected", reason);

        var nestedReason=evidence.NestedReference switch
        {
            OrdinaryNestedReferenceState.Relationship=>"nested_relationship",
            OrdinaryNestedReferenceState.ParentReferenced=>"nested_parent_referenced",
            OrdinaryNestedReferenceState.Invalid=>"nested_reference_invalid",_=>null
        };
        if(nestedReason is not null) return Result("protected",nestedReason,true,path);
        if (evidence.SharedOutputReference == SharedOutputPathReferenceState.Referenced)
            return Result("protected", "shared_output_path", true, path);

        if (evidence.PlannedOutputReference == PlannedOutputPathReferenceState.Indeterminate)
            throw new InvalidDataException("Retention planned path matching is unavailable.");
        if (evidence.PlannedOutputReference == PlannedOutputPathReferenceState.Referenced)
            return Result("protected", "planned_output_path", true, path);

        var pendingReason = evidence.PendingOutputUse switch
        {
            PendingOutputUseState.SetupExact => "setup_output_path",
            PendingOutputUseState.Template => "planned_output_template",
            PendingOutputUseState.Unknown => "pending_output_use_unknown",
            _ => null
        };
        if (pendingReason is not null) return Result("protected", pendingReason, true, path);

        if (evidence.PublicationAnchor is null) return Result("protected", "publication_identity_missing", true, path);
        if (!evidence.PublicationAnchor.Valid || evidence.PublicationAnchor.Length != item.OutputSizeBytes ||
            !string.Equals(evidence.PublicationAnchor.Path, path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid publication anchor evidence.");

        var bundleReason = item.Bundle?.Status switch
        {
            null => "bundle_evidence_unknown",
            "generating" => "bundle_generation_in_progress",
            "ready" => "bundle_artifact_reference",
            "failed" or "interrupted_unknown" => "bundle_artifact_state_unknown",
            "not_applicable" => null,
            _ => throw new InvalidDataException("Invalid bundle evidence.")
        };
        if (bundleReason is not null) return Result("protected", bundleReason, true, path);

        if (evidence.PublicationIdentityReference == PublicationIdentityReferenceState.Referenced)
            return Result("protected", "shared_publication_identity", true, path);

        if (occupied.Contains(path)) return Result("protected", "runtime_output_in_use", true, path);

        // A time-rule candidate may still be protected or have unknown bytes.
        // Never call it safe-to-delete, including when every check succeeds.
        try
        {
            foreach (var ancestor in checks!)
            {
                var attributes = _files.GetAttributes(ancestor);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return Result("protected", "reparse_point", true, path);
                if ((attributes & FileAttributes.Directory) == 0) return Result("protected", "parent_not_directory", true, path);
            }
        }
        catch (FileNotFoundException) { return Result("protected", "parent_missing", true, path); }
        catch (DirectoryNotFoundException) { return Result("protected", "parent_missing", true, path); }
        catch (UnauthorizedAccessException) { return Result("protected", "file_inaccessible", true, path); }
        catch (Exception) { return Result("protected", "metadata_unavailable", true, path); }

        FileMetadataObservation? observation;
        try { observation = _probe!.Observe(path); }
        catch (FileNotFoundException) { return Result("protected", "file_missing", true, path); }
        catch (DirectoryNotFoundException) { return Result("protected", "file_missing", true, path); }
        catch (UnauthorizedAccessException) { return Result("protected", "file_inaccessible", true, path); }
        catch (Exception) { return Result("protected", "metadata_unavailable", true, path); }
        // Contract damage is a page failure, distinct from an ordinary native
        // sampling failure represented by an explicit outcome.
        if (observation is null || !Enum.IsDefined(observation.Outcome))
            throw new InvalidDataException("Invalid retention file observation contract.");
        var observedReason = ObservationReason(observation, path, out var length);
        if (observedReason is not null) return Result("protected", observedReason, true, path);
        if (observation.After!.Identity != evidence.PublicationAnchor.Identity)
            return Result("protected", "publication_identity_mismatch", true, path);
        return Result(length == item.OutputSizeBytes ? "age_candidate" : "protected",
            length == item.OutputSizeBytes ? "age_rule_match" : "file_length_changed", true, path, length);
    }

    private string? ObservationReason(FileMetadataObservation observation, string requested, out long length)
    {
        length = 0;
        if (observation.Outcome != FileObservationOutcome.Sampled)
        {
            if (observation.Before is not null || observation.After is not null || observation.BeforePath is not null || observation.AfterPath is not null)
                throw new InvalidDataException("Invalid retention file observation payload.");
            return observation.Outcome switch
            {
                FileObservationOutcome.Missing => "file_missing", FileObservationOutcome.Inaccessible => "file_inaccessible",
                FileObservationOutcome.ReparsePoint => "reparse_point", FileObservationOutcome.Directory => "output_not_file",
                FileObservationOutcome.MetadataChanged => "metadata_changed", FileObservationOutcome.ResolvedPathUnavailable => "resolved_path_mismatch",
                _ => "file_identity_unavailable"
            };
        }
        var first = observation.Before; var second = observation.After;
        if (first is null || second is null) return "file_identity_unavailable";
        if (!WindowsReadOnlyFileMetadataProbe.ValidAttributes((uint)first.Attributes) ||
            !WindowsReadOnlyFileMetadataProbe.ValidAttributes((uint)second.Attributes)) return "file_identity_unavailable";
        if ((first.Attributes & FileAttributes.ReparsePoint) != 0) return "reparse_point";
        if ((first.Attributes & FileAttributes.Directory) != 0 || first.Directory == 1) return "output_not_file";
        if (first.Length < 0 || second.Length < 0 || (second.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || second.Directory == 1)
            return "metadata_changed";
        if (!ValidSnapshot(first) || !ValidSnapshot(second)) return "file_identity_unavailable";
        if (first.Links > 1 || second.Links > 1) return "hard_linked_output";
        if (first.DeletePending == 1 || second.DeletePending == 1 || first != second) return "file_identity_changed";
        if (!WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(observation.BeforePath) ||
            !WindowsReadOnlyFileMetadataProbe.IsCanonicalLocalPath(observation.AfterPath) ||
            !string.Equals(observation.BeforePath, requested, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observation.AfterPath, requested, StringComparison.OrdinalIgnoreCase) ||
            !observation.BeforePath!.StartsWith(_videosRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !observation.AfterPath!.StartsWith(_videosRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "resolved_path_mismatch";
        length = second.Length;
        return null;
    }
    private static bool ValidSnapshot(HandleMetadataSnapshot snapshot) => snapshot.AllocationSize >= 0 && snapshot.Links > 0 &&
        snapshot.DeletePending <= 1 && snapshot.Directory <= 1 && snapshot.Attributes != 0 &&
        ((uint)snapshot.Attributes & ~0x007fffffu) == 0 && (snapshot.Attributes & FileAttributes.Device) == 0 &&
        snapshot.LastWriteTime is > 0 and <= 2650467743999999999L && snapshot.Identity is { VolumeSerialNumber: > 0 } id &&
        (id.IdLow != 0 || id.IdHigh != 0);

    private string? ValidateScopedPath(string path, out IReadOnlyList<string>? checks)
    {
        checks = null;
        try
        {
            if (!LocalDrivePath(path) || !LocalDrivePath(_videosRoot)) return "path_invalid";
            UnifiedHistoryQueryService.ValidatePath(path); // Existing canonical evidence rules.
            if (!path.StartsWith(_videosRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "outside_videos";
            if (path.Length > MaxPathLength) return "path_check_limit";
            var root = Path.GetPathRoot(path)!;
            var parts = path[root.Length..].Split(Path.DirectorySeparatorChar);
            if (parts.Length > MaxPathComponents) return "path_check_limit";
            if (parts.Any(p => !ValidComponent(p))) return "path_invalid";
            var name = parts[^1];
            if (!name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || name.Length <= 4) return "not_mp4";
            var ancestors = new List<string> { root }; var current = root;
            foreach (var part in parts[..^1]) { current = Path.Combine(current, part); ancestors.Add(current); }
            checks = ancestors;
            return null;
        }
        catch { return "path_invalid"; }
    }
    private static bool LocalDrivePath(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
        !path.Contains('/') && !path.Contains('\0');
    private static bool ValidComponent(string part)
    {
        if (string.IsNullOrEmpty(part) || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
              (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }
}
