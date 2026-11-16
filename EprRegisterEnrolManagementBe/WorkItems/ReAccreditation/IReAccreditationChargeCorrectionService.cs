using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-766 (temporary, removed once the one-off correction is verified): sets
/// the registration charge held on a re-accreditation work item.
///
/// Module-scoped by name per the framework's DI rule: modules never share
/// interfaces.
/// </summary>
internal interface IReAccreditationChargeCorrectionService
{
    /// <summary>
    /// Set <c>payload.chargeAmountPence</c> on <paramref name="workItemId"/> to
    /// <paramref name="chargeAmountPence"/> when it differs, and record the
    /// change on the audit log. State and SLA clock are left untouched.
    /// </summary>
    Task<ChargeCorrectionResult> CorrectChargeAsync(
        Guid workItemId,
        int chargeAmountPence,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>The result of <see cref="IReAccreditationChargeCorrectionService.CorrectChargeAsync"/>.</summary>
internal sealed record ChargeCorrectionResult
{
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";

    /// <summary>Set when the correction was refused; every other member is then unset.</summary>
    public WorkItemActionResult? Failure { get; init; }

    /// <summary><see cref="Updated"/> or <see cref="Unchanged"/>.</summary>
    public string? Outcome { get; init; }

    public int ChargeAmountPence { get; init; }

    /// <summary>Whether duly making has recorded a payment date on the work item.</summary>
    public bool DulyMade { get; init; }

    public static ChargeCorrectionResult Refused(WorkItemActionResult failure) => new() { Failure = failure };
}
