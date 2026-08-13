using MongoDB.Bson;

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
    /// <c>A{Year:2}{Agency:1}{OperatorType:1}{OrgId:6}{PostcodeSuffix:3}{Material:2}</c>
    /// (16 characters, fixed width). The generator owns uniqueness:
    /// implementations must consult the persistence layer and, on
    /// collision, disambiguate rather than regenerate (the format is
    /// deterministic for a given payload/year), returning a value that
    /// does not yet exist on any persisted work item. When uniqueness
    /// cannot be established within a small bounded number of attempts the
    /// implementation throws so the calling approval service can surface a
    /// domain failure.
    /// </summary>
    /// <param name="payload">The work item's payload. Supplies the
    /// material, regulator postcode, waste-processing (operator) type and
    /// operator organisation id segments.</param>
    /// <param name="year">Four-digit accreditation year; only its last two
    /// digits form the year segment.</param>
    /// <param name="cancellationToken">Token to cancel the lookup.</param>
    Task<string> GenerateAsync(BsonDocument payload, int year, CancellationToken cancellationToken = default);
}
