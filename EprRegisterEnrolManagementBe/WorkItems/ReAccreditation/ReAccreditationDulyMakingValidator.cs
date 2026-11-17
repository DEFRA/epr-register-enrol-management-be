using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-316 payment-date validation for the bespoke duly-make endpoint.
///
/// RA-495: the rules are the shared <see cref="StandardDulyMakingValidator"/>;
/// this keeps re-accreditation's names for them. Every failure carries a
/// stable machine-readable <c>errorCode</c>, part of the wire contract with
/// management-fe and mgmt-tests.
/// </summary>
internal static class ReAccreditationDulyMakingValidator
{
    /// <summary>The only accepted wire format. An ISO timestamp is rejected.</summary>
    public const string DateFormat = StandardDulyMakingValidator.DateFormat;

    /// <summary>How far into the past a payment date may reach.</summary>
    public static readonly TimeSpan MaxAge = StandardDulyMakingValidator.MaxAge;

    public const string CodeRequired = StandardDulyMakingValidator.CodeRequired;
    public const string CodeInvalid = StandardDulyMakingValidator.CodeInvalid;
    public const string CodeInFuture = StandardDulyMakingValidator.CodeInFuture;
    public const string CodeTooOld = StandardDulyMakingValidator.CodeTooOld;

    /// <summary>The request field every failure is bound to.</summary>
    public const string Field = StandardDulyMakingValidator.Field;

    /// <inheritdoc cref="StandardDulyMakingValidator.Validate"/>
    public static ReAccreditationDulyMakingValidationResult Validate(
        string? paymentDate,
        DateOnly today
    )
    {
        var result = StandardDulyMakingValidator.Validate(paymentDate, today);
        return result.IsValid
            ? ReAccreditationDulyMakingValidationResult.Success(result.PaymentDate!.Value)
            : ReAccreditationDulyMakingValidationResult.Failure(result.ErrorCode!, result.Detail!);
    }
}

/// <summary>
/// Outcome of <see cref="ReAccreditationDulyMakingValidator.Validate"/>: either a
/// parsed <see cref="DateOnly"/>, or an error code plus developer-facing detail.
/// </summary>
internal sealed record ReAccreditationDulyMakingValidationResult
{
    private ReAccreditationDulyMakingValidationResult(
        DateOnly? paymentDate,
        string? errorCode,
        string? detail
    )
    {
        PaymentDate = paymentDate;
        ErrorCode = errorCode;
        Detail = detail;
    }

    public DateOnly? PaymentDate { get; }
    public string? ErrorCode { get; }
    public string? Detail { get; }

    public bool IsValid => ErrorCode is null;

    public static ReAccreditationDulyMakingValidationResult Success(DateOnly paymentDate) =>
        new(paymentDate, null, null);

    public static ReAccreditationDulyMakingValidationResult Failure(
        string errorCode,
        string detail
    ) => new(null, errorCode, detail);
}
