namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-800: the request-shape rules for changing a case's valid-from date: the
/// date is present and a real <c>yyyy-MM-dd</c> date, and the reason is present
/// and at most <see cref="MaxReasonWords"/> words (whitespace-separated). The
/// year bounds need the work item, so the service applies them
/// (<see cref="StandardEffectiveDate.CheckWithinAccreditationYear"/>).
///
/// Every failure carries a stable <c>errorCode</c> plus the <c>field</c> it
/// belongs to, the wire contract with management-fe. The date codes are the
/// determination's (<see cref="StandardEffectiveDate"/>).
/// </summary>
internal static class StandardChangeValidFromValidator
{
    public const int MaxReasonWords = 200;

    public const string CodeReasonRequired = "effective-date-reason-required";
    public const string CodeReasonTooLong = "effective-date-reason-too-long";

    public const string DateField = StandardEffectiveDate.Field;
    public const string ReasonField = "reason";

    public static StandardChangeValidFromValidation Validate(string? effectiveDate, string? reason)
    {
        if (!StandardEffectiveDate.TryParse(effectiveDate, out var parsed))
        {
            return StandardChangeValidFromValidation.Failure(
                StandardEffectiveDate.CodeInvalid,
                $"'{StandardEffectiveDate.Field}' must be a real {StandardEffectiveDate.DateFormat} date.",
                DateField
            );
        }

        if (parsed is not { } date)
        {
            return StandardChangeValidFromValidation.Failure(
                StandardEffectiveDate.CodeRequired,
                "A valid-from date is required.",
                DateField
            );
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return StandardChangeValidFromValidation.Failure(
                CodeReasonRequired,
                "A reason for changing the valid-from date is required.",
                ReasonField
            );
        }

        var words = reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return words > MaxReasonWords
            ? StandardChangeValidFromValidation.Failure(
                CodeReasonTooLong,
                $"The reason is {words} words; the maximum is {MaxReasonWords}.",
                ReasonField
            )
            : StandardChangeValidFromValidation.Success(date, reason.Trim());
    }
}

/// <summary>
/// Outcome of <see cref="StandardChangeValidFromValidator.Validate"/>: the parsed
/// date and trimmed reason, or an error code, developer-facing detail and field.
/// </summary>
internal sealed record StandardChangeValidFromValidation
{
    private StandardChangeValidFromValidation(
        DateOnly? effectiveDate,
        string? reason,
        string? errorCode,
        string? detail,
        string? field
    )
    {
        EffectiveDate = effectiveDate;
        Reason = reason;
        ErrorCode = errorCode;
        Detail = detail;
        Field = field;
    }

    public DateOnly? EffectiveDate { get; }
    public string? Reason { get; }
    public string? ErrorCode { get; }
    public string? Detail { get; }
    public string? Field { get; }

    public bool IsValid => ErrorCode is null;

    public static StandardChangeValidFromValidation Success(DateOnly effectiveDate, string reason) =>
        new(effectiveDate, reason, null, null, null);

    public static StandardChangeValidFromValidation Failure(string errorCode, string detail, string field) =>
        new(null, null, errorCode, detail, field);
}
