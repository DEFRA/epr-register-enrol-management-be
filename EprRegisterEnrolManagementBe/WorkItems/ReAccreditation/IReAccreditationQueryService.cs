using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-291 module-scoped service that raises a query against a
/// re-accreditation application. Module DI uses module-scoped interfaces so
/// the re-accreditation folder stays self-contained.
/// </summary>
internal interface IReAccreditationQueryService
{
    /// <summary>
    /// Assign the work item to the acting user, move it into <c>queried</c>,
    /// and record the queried sections plus the case worker's reason on the
    /// audit log.
    ///
    /// The self-assignment happens first (RA-291): the query page promises
    /// "the application will also be assigned to you", and a failed assign
    /// must leave the application un-queried rather than
    /// queried-but-unassigned.
    ///
    /// The caller never supplies an action id: the correct
    /// <c>query-during-*</c> transition is resolved from the work item's
    /// current state. A state with no query transition (already
    /// <c>queried</c>, or terminal) fails with
    /// <see cref="WorkItemActionFailureCode.InvalidTransition"/>, which the
    /// endpoint surfaces as a 409.
    /// </summary>
    Task<WorkItemActionResult> QueryAsync(
        Guid workItemId,
        IReadOnlyList<string> sections,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
