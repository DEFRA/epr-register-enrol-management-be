using System.Globalization;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495 AC6: the effective date a caseworker enters when making the
/// determination on a type that takes one
/// (<see cref="IStandardLifecycleStrategy.TakesEffectiveDate"/>, add-ORS only).
/// On an approval it is the date the operator's accreditation shows the site
/// as accredited from.
///
/// An approval must name it, and it may not be earlier than the UK calendar
/// date the request was submitted; otherwise a past or future date is allowed.
/// A refusal is not asked for one: it records the submission date, so the
/// application history and payload carry a date on either outcome (both
/// confirmed with the product owner, 2026-11-19).
/// </summary>
internal static class StandardEffectiveDate
{
    /// <summary>The wire format, as for the payment date: <c>yyyy-MM-dd</c>.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>The request field, and the top-level payload field it is stored on.</summary>
    public const string Field = "effectiveDate";

    public const string CodeRequired = "effective-date-required";
    public const string CodeInvalid = "effective-date-invalid";
    public const string CodeBeforeSubmission = "effective-date-before-submission";

    /// <summary>
    /// Parses the wire value. Absent is fine here (whether it is required
    /// depends on the type and the outcome); false only for a value that is
    /// present but not a real <c>yyyy-MM-dd</c> date.
    /// </summary>
    public static bool TryParse(string? value, out DateOnly? effectiveDate)
    {
        effectiveDate = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        // ParseExact, not TryParse: the wire format is a fixed contract, and a
        // culture-sensitive parse would read '03/04/2026' as two different days
        // depending on the server's locale.
        if (
            !DateOnly.TryParseExact(
                value.Trim(),
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed
            )
        )
        {
            return false;
        }

        effectiveDate = parsed;
        return true;
    }

    /// <summary>A refusal's effective date: the UK calendar date the request was submitted.</summary>
    public static DateOnly ForRefusal(WorkItem workItem) => UkCalendar.DateOf(workItem.SubmittedAt);

    /// <summary>
    /// An approval's rules against the work item: present, and on or after
    /// its submission date. Null when the date may be used, otherwise the
    /// failure to hand back.
    /// </summary>
    public static WorkItemActionResult? Check(WorkItem workItem, DateOnly? effectiveDate)
    {
        if (effectiveDate is not { } date)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidEffectiveDate,
                $"An effective date is required to approve work item {workItem.Id}.",
                CodeRequired
            );
        }

        var submittedOn = UkCalendar.DateOf(workItem.SubmittedAt);
        return date < submittedOn
            ? WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidEffectiveDate,
                $"Effective date {date:yyyy-MM-dd} is before work item {workItem.Id} was submitted "
                    + $"({submittedOn:yyyy-MM-dd}).",
                CodeBeforeSubmission
            )
            : null;
    }
}
