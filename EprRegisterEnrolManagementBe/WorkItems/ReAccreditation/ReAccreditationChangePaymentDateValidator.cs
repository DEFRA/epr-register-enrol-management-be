namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-576 validation for the bespoke change-payment-date endpoint.
///
/// The date rules are exactly the duly-making rules
/// (<see cref="ReAccreditationDulyMakingValidator"/>): a corrected payment date
/// is held to the same standard as the one it replaces. This type adds the
/// mandatory reason (AC03).
///
/// As with duly making, every failure carries a stable <c>errorCode</c> plus the
/// <c>field</c> it belongs to; both are part of the wire contract with
/// management-fe and mgmt-tests.
/// </summary>
internal static class ReAccreditationChangePaymentDateValidator
{
    public const int MaxReasonWords = 200;

    public const string CodeReasonRequired = "payment-date-reason-required";
    public const string CodeReasonTooLong = "payment-date-reason-too-long";

    public const string ReasonField = "reason";

    public static ReAccreditationChangePaymentDateValidationResult Validate(
        string? paymentDate,
        string? reason,
        DateOnly today
    )
    {
        var date = ReAccreditationDulyMakingValidator.Validate(paymentDate, today);
        if (!date.IsValid)
        {
            return ReAccreditationChangePaymentDateValidationResult.Failure(
                date.ErrorCode!,
                date.Detail!,
                ReAccreditationDulyMakingValidator.Field
            );
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return ReAccreditationChangePaymentDateValidationResult.Failure(
                CodeReasonRequired,
                "A reason for changing the payment date is required.",
                ReasonField
            );
        }

        var words = reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words > MaxReasonWords)
        {
            return ReAccreditationChangePaymentDateValidationResult.Failure(
                CodeReasonTooLong,
                $"The reason is {words} words; the maximum is {MaxReasonWords}.",
                ReasonField
            );
        }

        return ReAccreditationChangePaymentDateValidationResult.Success(
            date.PaymentDate!.Value,
            reason.Trim()
        );
    }
}

/// <summary>
/// Outcome of <see cref="ReAccreditationChangePaymentDateValidator.Validate"/>:
/// either the parsed date and trimmed reason, or an error code, developer-facing
/// detail and the field the error belongs to.
/// </summary>
internal sealed record ReAccreditationChangePaymentDateValidationResult
{
    private ReAccreditationChangePaymentDateValidationResult(
        DateOnly? paymentDate,
        string? reason,
        string? errorCode,
        string? detail,
        string? field
    )
    {
        PaymentDate = paymentDate;
        Reason = reason;
        ErrorCode = errorCode;
        Detail = detail;
        Field = field;
    }

    public DateOnly? PaymentDate { get; }
    public string? Reason { get; }
    public string? ErrorCode { get; }
    public string? Detail { get; }
    public string? Field { get; }

    public bool IsValid => ErrorCode is null;

    public static ReAccreditationChangePaymentDateValidationResult Success(
        DateOnly paymentDate,
        string reason
    ) => new(paymentDate, reason, null, null, null);

    public static ReAccreditationChangePaymentDateValidationResult Failure(
        string errorCode,
        string detail,
        string field
    ) => new(null, null, errorCode, detail, field);
}
