using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Default <see cref="IReAccreditationDecisionService"/>. Encodes the PoC
/// recommendation rules as a small, side-effect-free function over the
/// payload. Real rules will replace this implementation when the
/// re-accreditation policy is finalised.
/// </summary>
public sealed class ReAccreditationDecisionService : IReAccreditationDecisionService
{
    /// <summary>
    /// Maximum number of historical compliance issues tolerated before the
    /// service recommends rejection.
    /// </summary>
    public const int MaxToleratedComplianceIssues = 2;

    public ReAccreditationRecommendation EvaluateRecommendation(ReAccreditationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.PreviousAccreditationYear is null
            || payload.ComplianceIssuesReported is null
            || payload.MaterialsHandled is null
            || payload.MaterialsHandled.Count == 0)
        {
            return new ReAccreditationRecommendation(
                ReAccreditationRecommendation.MoreInfoNeeded,
                "Payload is missing fields required to evaluate the application.");
        }

        if (payload.ComplianceIssuesReported > MaxToleratedComplianceIssues)
        {
            return new ReAccreditationRecommendation(
                ReAccreditationRecommendation.Reject,
                $"Reported {payload.ComplianceIssuesReported} compliance issues " +
                $"(threshold is {MaxToleratedComplianceIssues}).");
        }

        return new ReAccreditationRecommendation(
            ReAccreditationRecommendation.Approve,
            "Compliance history within tolerance and required fields present.");
    }
}