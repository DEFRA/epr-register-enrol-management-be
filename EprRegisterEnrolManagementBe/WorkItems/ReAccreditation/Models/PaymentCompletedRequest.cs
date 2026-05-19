using System.ComponentModel.DataAnnotations;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// Request body for <c>POST /work-items/{id}/payment-completed</c>.
/// Fields are supplied by the operator backend when the operator's payment
/// is confirmed; they are used to stamp the SLA clock and attribute audit
/// entries to the paying operator rather than to the regulator.
/// </summary>
public sealed record PaymentCompletedRequest
{
    [Required]
    public long AmountPence { get; init; }

    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Reference { get; init; }

    /// <summary>UTC timestamp the payment was recorded by the payment provider.</summary>
    [Required]
    public DateTime PaidAt { get; init; }

    /// <summary>Operator platform user id — used to attribute audit entries.</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string PaidByUserId { get; init; }

    /// <summary>Email address of the paying operator — used as the Notify recipient.</summary>
    [Required]
    [EmailAddress]
    public required string PaidByEmail { get; init; }
}
