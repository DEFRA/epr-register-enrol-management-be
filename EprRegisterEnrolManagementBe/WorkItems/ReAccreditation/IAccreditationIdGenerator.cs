namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-133 (supersedes RA-132): factory for the human-facing accreditation
/// identifier stamped on a re-accreditation work item when it is approved.
/// Pulled behind an interface so the approval service can be unit-tested
/// with a deterministic generator.
/// </summary>
public interface IAccreditationIdGenerator
{
    /// <summary>
    /// Produce a fresh accreditation id of the shape
    /// <c>ACC-{Year}-{Material[:1]}-{ULID8}</c>. The generator owns
    /// uniqueness: implementations must consult the persistence layer
    /// and regenerate on collision, returning a value that does not yet
    /// exist on any persisted work item. When uniqueness cannot be
    /// established within a small bounded number of attempts the
    /// implementation throws so the calling approval service can
    /// surface a domain failure.
    /// </summary>
    /// <param name="material">First material the work item handles.
    /// Its uppercase first character forms the material segment;
    /// when null/empty the literal <c>X</c> is used.</param>
    /// <param name="year">Four-digit accreditation year segment.</param>
    /// <param name="cancellationToken">Token to cancel the lookup.</param>
    Task<string> GenerateAsync(string? material, int year, CancellationToken cancellationToken = default);
}
