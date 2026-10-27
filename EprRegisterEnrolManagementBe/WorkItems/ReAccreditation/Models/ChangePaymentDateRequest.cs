namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-576 request body for
/// <c>POST /work-items/re-accreditation/{id}/change-payment-date</c>.
///
/// Both members are typed as <see cref="string"/> for the same reason as
/// <see cref="DulyMakeRequest.PaymentDate"/>: a binder-level failure would be a
/// framework 400 with no machine-readable code, so the validator owns every
/// failure and attaches a stable <c>errorCode</c> the frontend can bind to a
/// field.
/// </summary>
public sealed record ChangePaymentDateRequest
{
    /// <summary>The corrected payment date, as a plain <c>yyyy-MM-dd</c> string.</summary>
    public string? PaymentDate { get; init; }

    /// <summary>Why the payment date is being changed. Recorded in the audit log.</summary>
    public string? Reason { get; init; }
}
