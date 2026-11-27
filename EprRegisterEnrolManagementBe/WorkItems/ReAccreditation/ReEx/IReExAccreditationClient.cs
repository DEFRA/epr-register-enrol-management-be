using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;

/// <summary>
/// Fetches prior-year accreditation data from the ReEx registry for a
/// re-accreditation work item.
/// </summary>
public interface IReExAccreditationClient
{
    /// <summary>
    /// Fetch the accreditation for the given organisation / registration /
    /// year combination. <paramref name="organisationId"/> and
    /// <paramref name="registrationId"/> are the ReEx-native identifiers
    /// carried in the work item payload; <paramref name="year"/> is the prior
    /// accreditation year to look up. Found with the accreditation; NotFound
    /// when ReEx holds no such organisation, registration or accreditation for
    /// that year; Unavailable when ReEx cannot answer (logged at Error). Throws
    /// <see cref="ArgumentException"/> for a blank identifier.
    /// </summary>
    Task<PriorYearReadResult> GetPriorYearAsync(
        string organisationId,
        string registrationId,
        int year,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// RA-526: the Nation the given registration's own <c>submittedToRegulator</c>
    /// maps to, via <see cref="RegulatorNationMapper"/> — used by
    /// <see cref="ReAccreditationNationCorrectionMigration"/> to correct work items
    /// whose <c>payload.nation</c> was derived by the pre-RA-526 postcode-based
    /// hook. NotFound when ReEx holds no such organisation or registration;
    /// Unavailable when ReEx cannot answer (logged at Error). Throws
    /// <see cref="ArgumentException"/> for a blank identifier.
    /// </summary>
    Task<NationReadResult> GetNationAsync(
        string organisationId,
        string registrationId,
        CancellationToken cancellationToken = default);
}
