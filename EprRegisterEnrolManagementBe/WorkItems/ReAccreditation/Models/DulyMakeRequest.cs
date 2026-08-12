namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-316 request body for
/// <c>POST /work-items/re-accreditation/{id}/duly-make</c>.
///
/// Deliberately carries only the payment date. The charge amount and payment
/// reference shown on the duly-making page are read-only values sourced from
/// the work item payload, so a caller has no business posting them back —
/// accepting them would let a hand-crafted POST rewrite the charge the
/// regulator was shown.
/// </summary>
public sealed record DulyMakeRequest
{
    /// <summary>
    /// The date the operator paid, as a plain <c>yyyy-MM-dd</c> date string.
    ///
    /// Typed as <see cref="string"/> rather than <see cref="DateOnly"/> on
    /// purpose: a binder-level parse failure produces a framework 400 with no
    /// machine-readable code, and the case management frontend needs to tell
    /// "the regulator typed a bad date" (render a GOV.UK error summary against
    /// the date input) from "something broke" (page-level error). Taking the
    /// raw string lets <see cref="ReAccreditationDulyMakingValidator"/> own
    /// every failure and attach a stable <c>errorCode</c>.
    /// </summary>
    public string? PaymentDate { get; init; }
}
