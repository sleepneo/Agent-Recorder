using AgentRecorder.Api;
using AgentRecorder.Core.Automation;
using AgentRecorder.Infrastructure;
using AgentRecorder.Persistence;

namespace AgentRecorder.App;

internal sealed class SqliteFixedRegionProfileManagementGateway : IFixedRegionProfileSelectionCreationGateway
{
    private readonly SqliteFixedRegionProfileManagementRepository _repository;

    public SqliteFixedRegionProfileManagementGateway(SqliteOperationalStore store) =>
        _repository = new SqliteFixedRegionProfileManagementRepository(store);

    public FixedRegionProfileCreateResult CreateFromExisting(string idempotencyKey, string requestHash,
        ProfileRef sourceRef, string name, FixedRegionProfileChanges changes, DateTimeOffset nowUtc) =>
        Execute(() => _repository.CreateFromExisting(idempotencyKey, requestHash, sourceRef, name, changes, nowUtc));

    public FixedRegionProfileCreateResult? ReadCreateReplay(string idempotencyKey, string requestHash) =>
        Execute(() => _repository.ReadCreateReplay(idempotencyKey, requestHash));

    public FixedRegionProfileCreateResult CreateFromSelection(string idempotencyKey, string requestHash,
        string name, RecurringFixedRegionProfileSpecification specification, DateTimeOffset nowUtc) =>
        Execute(() => _repository.CreateFromSelection(idempotencyKey, requestHash, name, specification, nowUtc));

    public FixedRegionProfileDirectoryPage List(int limit, string? afterProfileId, bool includeDeleted) =>
        Execute(() => _repository.List(limit, afterProfileId, includeDeleted));

    public FixedRegionProfileDirectoryRecord? Get(string profileId) => Execute(() => _repository.Get(profileId));

    public FixedRegionProfileVersionPageSnapshot? ReadVersionPageSnapshot(
        string profileId, int limit, long? beforeVersionExclusive) =>
        Execute(() => _repository.ReadVersionPageSnapshot(profileId, limit, beforeVersionExclusive));

    public FixedRegionProfileExactVersionSnapshot? ReadExactVersionSnapshot(string profileId, long version) =>
        Execute(() => _repository.ReadExactVersionSnapshot(profileId, version));

    public FixedRegionProfileDirectoryRecord Patch(string profileId, string ifMatch, string? name,
        FixedRegionProfileChanges changes, DateTimeOffset nowUtc) =>
        Execute(() => _repository.Patch(profileId, ifMatch, name, changes, nowUtc));

    public void Delete(string profileId, string ifMatch, DateTimeOffset nowUtc) =>
        Execute(() => { _repository.Delete(profileId, ifMatch, nowUtc); return true; });

    private static T Execute<T>(Func<T> action)
    {
        try { return action(); }
        catch (ApiException) { throw; }
        catch (FixedRegionProfileInUseException exception)
        {
            throw new ApiException(409, "PROFILE_IN_USE", exception.Message,
                new { reason_code = "profile_in_use", reference_count = exception.ReferenceCount,
                    references = exception.References.Select(reference => new
                    { plan_id = reference.PlanId, profile_version = reference.ProfileVersion, profile_digest = reference.ProfileDigest }).ToArray() });
        }
        catch (Phase3PersistenceException exception)
        {
            if (exception.Code is FixedRegionProfileManagementReasonCodes.NotFound or RecurringFixedRegionProfilePersistenceReasonCodes.ProfileNotFound)
                throw new ApiException(404, "PROFILE_NOT_FOUND", "The profile was not found.");
            if (exception.Code == FixedRegionProfileManagementReasonCodes.Deleted)
                throw new ApiException(409, "PROFILE_DELETED", "The profile is tombstoned and cannot be changed or copied.", new { reason_code = "profile_deleted" });
            if (exception.Code is FixedRegionProfileManagementReasonCodes.PreconditionFailed or RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionStale)
                throw new ApiException(412, "PRECONDITION_FAILED", "The profile no longer matches If-Match.", new { reason_code = "profile_etag_mismatch" });
            if (exception.Code == FixedRegionProfileManagementReasonCodes.IdempotencyKeyReused)
                throw new ApiException(409, "IDEMPOTENCY_KEY_REUSED", exception.Message, new { reason_code = "idempotency_key_reused" });
            if (exception.Code is "profile_invalid" or "invalid_argument" or "profile_ref_mismatch" or
                RecurringFixedRegionProfilePersistenceReasonCodes.ProfileRefMismatch or RecurringFixedRegionProfilePersistenceReasonCodes.ProfileVersionConflict)
                throw new ApiException(400, "INVALID_ARGUMENT", "The profile request conflicts with the supported immutable profile contract.", new { reason_code = "profile_request_invalid" });
            throw new ApiException(500, "PROFILE_STORAGE_FAILED", "The profile operation could not be durably completed.", new { reason_code = "profile_storage_failed" });
        }
    }
}
