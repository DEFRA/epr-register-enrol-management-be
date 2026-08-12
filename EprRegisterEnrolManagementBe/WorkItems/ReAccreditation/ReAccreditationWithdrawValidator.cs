using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-252 request validation for the bespoke withdraw endpoint. Returns the
/// human-readable failure detail, or <c>null</c> when the request is valid.
/// Reuses <see cref="QueryReasonWordCounter"/>'s counting rule — the withdraw
/// reason shares the same 200-word cap and frontend character-count
/// component as the query reason, so the two must not drift.
/// </summary>
internal static class ReAccreditationWithdrawValidator
{
    public const int MaxReasonWords = ReAccreditationQueryValidator.MaxReasonWords;

    public const string MissingReasonMessage = "Enter a reason for the withdrawal";
    public const string ReasonTooLongMessage = "Reason must be 200 words or fewer";

    public static string? Validate(WithdrawApplicationRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.Reason))
        {
            return MissingReasonMessage;
        }

        if (QueryReasonWordCounter.CountWords(request.Reason) > MaxReasonWords)
        {
            return ReasonTooLongMessage;
        }

        return null;
    }
}
