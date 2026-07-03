using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx.Dtos;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.ReEx;

/// <summary>
/// Fetches prior-year accreditation data from the ReEx registry for a
/// re-accreditation work item. Returns null when the data is unavailable
/// (organisation not found, no matching prior-year accreditation, or the
/// call fails).
/// </summary>
public interface IReExAccreditationClient
{
    /// <summary>
    /// Fetch the accreditation for the given organisation / registration /
    /// year combination. <paramref name="organisationId"/> and
    /// <paramref name="registrationId"/> are the ReEx-native identifiers
    /// carried in the work item payload. Returns null if any parameter is
    /// missing or if the remote call fails / returns no result.
    /// </summary>
    Task<PriorYearAccreditationDto?> GetPriorYearAsync(
        string? organisationId,
        string? registrationId,
        int? year,
        CancellationToken cancellationToken = default);
}
