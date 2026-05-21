namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-133: read-only lookup used by <see cref="AccreditationIdGenerator"/>
/// to enforce cross-work-item uniqueness of issued accreditation ids.
/// Implementations check whether any persisted work item already carries
/// the supplied id in its <c>payload.accreditationId</c> field.
/// </summary>
public interface IAccreditationIdLookup
{
    /// <summary>
    /// Returns <c>true</c> when at least one persisted work item already
    /// has <c>payload.accreditationId</c> equal to
    /// <paramref name="accreditationId"/>.
    /// </summary>
    Task<bool> ExistsAsync(string accreditationId, CancellationToken cancellationToken = default);
}
