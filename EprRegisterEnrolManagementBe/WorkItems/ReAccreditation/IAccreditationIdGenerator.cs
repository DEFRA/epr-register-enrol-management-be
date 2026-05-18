namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132: factory for the human-facing accreditation identifier stamped
/// on a re-accreditation work item when it is approved. Pulled behind an
/// interface so the approval service can be unit-tested with a
/// deterministic generator.
/// </summary>
public interface IAccreditationIdGenerator
{
    /// <summary>
    /// Produce a fresh accreditation id. Implementations must return a
    /// value that is, in practice, unique across applications — collisions
    /// would force a regulator to manually disambiguate two approvals.
    /// </summary>
    string Generate();
}
