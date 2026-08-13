namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-410: the determination a caseworker recorded on the "Log decision" page.
///
/// Deliberately a closed two-value set rather than a free-text state id: the
/// caller picks an outcome, never a destination. Which transitions get applied
/// to reach it — and in which order — is
/// <see cref="ReAccreditationLogDecisionService"/>'s business.
///
/// <see cref="Rejected"/> is spelled to match the <c>rejected</c> state id and
/// the <c>reject</c> action id it produces, not the regulator-facing "Refused"
/// wording. That label lives in the case management frontend; renaming
/// anything here would break every stored audit entry and notification
/// template that names the existing ids.
/// </summary>
internal enum ReAccreditationDecisionOutcome
{
    Approved,
    Rejected,
}
