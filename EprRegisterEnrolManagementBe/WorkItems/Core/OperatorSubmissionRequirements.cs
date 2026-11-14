using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// The fields a submission from the operator backend must carry
/// (see "Operator backend submissions" in docs/work-items.md).
/// <see cref="WorkItemService"/> rejects the submission with a 400 when any is
/// missing or invalid, before a reference is generated or anything is stored.
/// Applies to every work-item type the operator backend submits: a
/// re-accreditation application and an in-year change request alike.
/// </summary>
internal static class OperatorSubmissionRequirements
{
    public const string PaymentReferenceField = "paymentReference";

    internal const string MissingOrgNumberMessage =
        "A submission from the operator backend must include operatorOrgNumber "
        + "as a whole number from 1 to 999999.";

    internal const string MissingPaymentReferenceMessage =
        "A submission from the operator backend must include a non-blank paymentReference string.";

    /// <summary>
    /// The fields <paramref name="payload"/> is missing or carries invalid
    /// values for, each with the message the caller gets. Empty when valid.
    /// </summary>
    public static IReadOnlyList<(string Field, string Message)> FindProblems(BsonDocument payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var problems = new List<(string Field, string Message)>();
        if (!OperatorOrgNumber.TryRead(payload, out _))
        {
            problems.Add((OperatorOrgNumber.Field, MissingOrgNumberMessage));
        }

        if (ReadPaymentReference(payload) is null)
        {
            problems.Add((PaymentReferenceField, MissingPaymentReferenceMessage));
        }

        return problems;
    }

    /// <summary>The payload's <c>paymentReference</c> when it is a non-blank string, else null.</summary>
    public static string? ReadPaymentReference(BsonDocument payload) =>
        payload.TryGetValue(PaymentReferenceField, out var value)
        && value.IsString
        && !string.IsNullOrWhiteSpace(value.AsString)
            ? value.AsString
            : null;
}

/// <summary>
/// Reads <c>payload.operatorOrgNumber</c>, the numeric ReEx organisation number
/// (e.g. <c>500500</c>). One rule, shared by the submission check and the
/// application reference generator, so the two can never disagree.
/// </summary>
internal static class OperatorOrgNumber
{
    public const string Field = "operatorOrgNumber";

    // Reject 0 (an unset value, never a real organisation number) and negatives (RA-503: a '-'
    // would land in the reference), reject non-integral, and cap at 999999 so D6's zero-padding
    // (a MINIMUM width, not a maximum) can never widen the reference segment past 6 digits. See
    // BsonWholeNumber for why the BSON type category alone cannot be trusted.
    private const int Min = 1;
    private const int Max = 999999;

    public static bool TryRead(BsonDocument payload, out int orgNumber)
    {
        orgNumber = 0;
        return payload.TryGetValue(Field, out var value)
            && BsonWholeNumber.TryGet(value, Min, Max, out orgNumber);
    }
}
