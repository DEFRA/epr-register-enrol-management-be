using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-768: removes named overseas sites from a re-accreditation work item's
/// <c>payload.overseasSites.sites</c>. The operator backend's one-off migration
/// calls it for work items whose query resubmission (before RA-557) dropped
/// sites that the work item still holds.
///
/// Module-scoped by name per the framework's DI rule: modules never share
/// interfaces.
/// </summary>
internal interface IReAccreditationOverseasSitesCorrectionService
{
    /// <summary>
    /// Remove every stored site matched by <paramref name="sites"/> and, when at
    /// least one was removed, record an <c>overseas-sites-corrected</c> audit
    /// entry carrying <paramref name="reason"/>. Other sites, every other
    /// payload field, state and SLA clock are left untouched.
    /// </summary>
    Task<OverseasSitesRemovalResult> RemoveSitesAsync(
        Guid workItemId,
        IReadOnlyList<OverseasSiteReference> sites,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>The result of <see cref="IReAccreditationOverseasSitesCorrectionService.RemoveSitesAsync"/>.</summary>
internal sealed record OverseasSitesRemovalResult
{
    /// <summary>The work item is approved, rejected, withdrawn or awaiting applicant contact.</summary>
    public const string DecidedReason = "decided";

    /// <summary>The work item has a payment date: duly making has been completed.</summary>
    public const string DulyMadeReason = "duly-made";

    /// <summary>The work item kept changing under the write; the caller can retry.</summary>
    public const string ConcurrencyConflictReason = "concurrency-conflict";

    /// <summary>Set when the removal was refused; the site lists are then empty.</summary>
    public WorkItemActionResult? Failure { get; init; }

    /// <summary>
    /// For a 409 refusal, which of <see cref="DecidedReason"/>,
    /// <see cref="DulyMadeReason"/> or <see cref="ConcurrencyConflictReason"/> applies.
    /// </summary>
    public string? ConflictReason { get; init; }

    /// <summary>The stored sites that were removed, with their stored ids.</summary>
    public IReadOnlyList<OverseasSiteReference> Removed { get; init; } = [];

    /// <summary>The request entries that matched no stored site.</summary>
    public IReadOnlyList<OverseasSiteReference> AlreadyAbsent { get; init; } = [];

    public static OverseasSitesRemovalResult Refused(
        WorkItemActionFailureCode code,
        string message,
        string? conflictReason = null
    ) =>
        new()
        {
            Failure = WorkItemActionResult.Failure(code, message),
            ConflictReason = conflictReason,
        };
}
