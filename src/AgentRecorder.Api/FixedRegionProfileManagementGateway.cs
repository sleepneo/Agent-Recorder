using AgentRecorder.Core.Automation;
using AgentRecorder.Core;

namespace AgentRecorder.Api;

/// <summary>Host-composed durable profile management operations. A missing gateway means unavailable.</summary>
public interface IFixedRegionProfileManagementGateway
{
    FixedRegionProfileCreateResult CreateFromExisting(string idempotencyKey, string requestHash,
        ProfileRef sourceRef, string name, FixedRegionProfileChanges changes, DateTimeOffset nowUtc);
    FixedRegionProfileDirectoryPage List(int limit, string? afterProfileId, bool includeDeleted);
    FixedRegionProfileDirectoryRecord? Get(string profileId);
    FixedRegionProfileVersionPageSnapshot? ReadVersionPageSnapshot(string profileId, int limit, long? beforeVersionExclusive);
    FixedRegionProfileExactVersionSnapshot? ReadExactVersionSnapshot(string profileId, long version);
    FixedRegionProfileDirectoryRecord Patch(string profileId, string ifMatch, string? name,
        FixedRegionProfileChanges changes, DateTimeOffset nowUtc);
    void Delete(string profileId, string ifMatch, DateTimeOffset nowUtc);
}

/// <summary>Additional creation operations for first-profile local selection provenance.</summary>
public interface IFixedRegionProfileSelectionCreationGateway : IFixedRegionProfileManagementGateway
{
    FixedRegionProfileCreateResult? ReadCreateReplay(string idempotencyKey, string requestHash);
    FixedRegionProfileCreateResult CreateFromSelection(string idempotencyKey, string requestHash,
        string name, RecurringFixedRegionProfileSpecification specification, DateTimeOffset nowUtc);
}

internal interface IFixedRegionProfileDisplayEnvironmentProvider
{
    IReadOnlyList<StandingLeaseDisplayMetadata> GetExecutionMetadata();
    IReadOnlyList<DisplayTopologySnapshot> GetTopology();
}

internal sealed class SystemQueryFixedRegionProfileDisplayEnvironmentProvider
    : IFixedRegionProfileDisplayEnvironmentProvider
{
    internal static readonly SystemQueryFixedRegionProfileDisplayEnvironmentProvider Instance = new();
    private SystemQueryFixedRegionProfileDisplayEnvironmentProvider() { }

    public IReadOnlyList<StandingLeaseDisplayMetadata> GetExecutionMetadata() =>
        SystemQueryDisplayTopologyProvider.Instance.GetCurrentExecutionMetadata();

    public IReadOnlyList<DisplayTopologySnapshot> GetTopology() =>
        SystemQueryDisplayTopologyProvider.Instance.GetCurrentDisplays();
}
