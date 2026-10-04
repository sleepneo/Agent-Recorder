using System.Security.Cryptography;
using System.Text;

namespace AgentRecorder.Core.Automation;

/// <summary>Mutable directory metadata for an immutable fixed-region profile history.</summary>
public sealed record FixedRegionProfileDirectoryRecord(
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DeletedAtUtc,
    RecurringFixedRegionProfileVersion CurrentVersion)
{
    public string ProfileId => CurrentVersion.ProfileId;
    public bool IsDeleted => DeletedAtUtc.HasValue;
}

public sealed record FixedRegionProfileDirectoryPage(
    IReadOnlyList<FixedRegionProfileDirectoryRecord> Items,
    string? NextCursor);

public sealed record FixedRegionProfileVersionPage(
    IReadOnlyList<RecurringFixedRegionProfileVersion> Items,
    string? NextCursor);

/// <summary>
/// Directory metadata and a bounded version page read from one database
/// snapshot. The directory's current version is the current-ref evidence.
/// </summary>
public sealed record FixedRegionProfileVersionPageSnapshot(
    FixedRegionProfileDirectoryRecord Directory,
    FixedRegionProfileVersionPage Page);

/// <summary>
/// Directory metadata and an optional exact historical version read from one
/// database snapshot. A null Version means it did not exist in this snapshot.
/// </summary>
public sealed record FixedRegionProfileExactVersionSnapshot(
    FixedRegionProfileDirectoryRecord Directory,
    RecurringFixedRegionProfileVersion? Version);

public sealed record FixedRegionProfileCreateResult(
    FixedRegionProfileDirectoryRecord Profile,
    bool Replayed,
    string ETag);

/// <summary>Only the four explicitly managed values can vary in this API slice.</summary>
public sealed record FixedRegionProfileChanges(
    int? DurationSeconds = null,
    int? CountdownSeconds = null,
    string? OutputDirectory = null,
    string? FilenamePrefix = null);

public static class FixedRegionProfileETag
{
    public static string Compute(string profileId, string name, long version, string digest, bool isDeleted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        var canonical = string.Join('\n', "fixed-region-profile-management/v1", profileId, name, version.ToString(System.Globalization.CultureInfo.InvariantCulture), digest, isDeleted ? "deleted" : "active");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return $"\"profile-v1-{hash}\"";
    }

    public static string Compute(FixedRegionProfileDirectoryRecord profile) =>
        Compute(profile.ProfileId, profile.Name, profile.CurrentVersion.ProfileVersion,
            profile.CurrentVersion.ProfileDigest, profile.IsDeleted);
}
