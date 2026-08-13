using System.Globalization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-316 payment-date validation for the bespoke duly-make endpoint.
///
/// Kept separate from <see cref="ReAccreditationDulyMakingService"/> — mirroring
/// <c>ReAccreditationQueryValidator</c> and <c>ReAccreditationWithdrawValidator</c>
/// — so the rules are unit-testable without a persistence substitute.
///
/// Every failure carries a stable machine-readable <c>errorCode</c>. That is the
/// contract with the case management frontend: it owns the user-facing copy and
/// switches on the code, and the human-readable detail returned alongside is
/// developer-facing only. Codes are therefore part of the wire contract and must
/// not be renamed without telling management-fe and mgmt-tests.
/// </summary>
internal static class ReAccreditationDulyMakingValidator
{
    /// <summary>The only accepted wire format. An ISO timestamp is rejected.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// How far into the past a payment date may reach.
    ///
    /// A payment date EARLIER than the application's submission date is
    /// deliberately allowed: a regulator recording a payment that genuinely
    /// landed before the application reached case management is a real case, and
    /// refusing it would strand the application with no workaround. But an
    /// unbounded past date is not harmless either — the SLA clock is anchored to
    /// it, so a mistyped year silently creates a 12-week clock that is already
    /// breached by months, which reads as a process failure rather than a typo.
    /// A twelve-month floor admits every plausible real payment while catching
    /// the wrong-year slip, which is the only realistic way to land here.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365);

    public const string CodeRequired = "payment-date-required";
    public const string CodeInvalid = "payment-date-invalid";
    public const string CodeInFuture = "payment-date-in-future";
    public const string CodeTooOld = "payment-date-too-old";

    /// <summary>The field all four codes above refer to.</summary>
    public const string Field = "paymentDate";

    /// <summary>
    /// Parse and validate <paramref name="paymentDate"/> against
    /// <paramref name="today"/> (the caller supplies it from the injected
    /// <see cref="TimeProvider"/> so tests can pin the boundary).
    ///
    /// <paramref name="today"/> itself is valid — a regulator recording a
    /// payment made this morning must not be turned away.
    /// </summary>
    public static ReAccreditationDulyMakingValidationResult Validate(
        string? paymentDate,
        DateOnly today
    )
    {
        if (string.IsNullOrWhiteSpace(paymentDate))
        {
            return ReAccreditationDulyMakingValidationResult.Failure(
                CodeRequired,
                "A payment date is required."
            );
        }

        // ParseExact, not TryParse: the wire format is a fixed contract, and a
        // culture-sensitive parse would read '03/04/2026' as two different days
        // depending on the server's locale.
        if (
            !DateOnly.TryParseExact(
                paymentDate.Trim(),
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed
            )
        )
        {
            return ReAccreditationDulyMakingValidationResult.Failure(
                CodeInvalid,
                $"Payment date '{paymentDate}' is not a valid {DateFormat} date."
            );
        }

        if (parsed > today)
        {
            return ReAccreditationDulyMakingValidationResult.Failure(
                CodeInFuture,
                $"Payment date {parsed:yyyy-MM-dd} is in the future; today is {today:yyyy-MM-dd}."
            );
        }

        var floor = today.AddDays(-MaxAge.Days);
        if (parsed < floor)
        {
            return ReAccreditationDulyMakingValidationResult.Failure(
                CodeTooOld,
                $"Payment date {parsed:yyyy-MM-dd} is more than {MaxAge.Days} days ago; "
                    + $"the earliest accepted date is {floor:yyyy-MM-dd}."
            );
        }

        return ReAccreditationDulyMakingValidationResult.Success(parsed);
    }
}

/// <summary>
/// Outcome of <see cref="ReAccreditationDulyMakingValidator.Validate"/>: either a
/// parsed date, or an error code plus developer-facing detail.
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
