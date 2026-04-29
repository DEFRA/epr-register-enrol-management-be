using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Module-scoped service object demonstrating where re-accreditation business
/// logic lives. The framework's <see cref="Core.IWorkItemService"/> drives
/// universal task / transition rules; type-specific reasoning over the
/// payload (e.g. "should we recommend approving this?") belongs here.
/// </summary>
public interface IReAccreditationDecisionService
{
    /// <summary>
    /// Pure, deterministic recommendation derived from the payload alone. The
    /// caller decides what to do with the recommendation (display it to an
    /// assessor, drive automation, etc.); this service has no I/O and no
    /// engine side effects.
    /// </summary>
    ReAccreditationRecommendation EvaluateRecommendation(ReAccreditationPayload payload);
}

/// <summary>Outcome the service recommends, plus a one-line rationale.</summary>
public sealed record ReAccreditationRecommendation(string Outcome, string Rationale)
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string MoreInfoNeeded = "more-info-needed";
}
