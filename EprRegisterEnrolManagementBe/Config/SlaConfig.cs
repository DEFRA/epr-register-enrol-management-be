namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// RA-131: bounds on SLA extension / override operations. Bound from the
/// <c>WorkItems:Sla</c> configuration section. The default ceiling of
/// 31 days per call lets a team leader absorb a single calendar month
/// of justified delay without manual approval routes, but blocks open-
/// ended overrides that would render the SLA meaningless.
/// </summary>
public sealed class SlaConfig
{
    /// <summary>
    /// Maximum number of days a single extend call may add, and the
    /// maximum number of days an override call may grow
    /// <see cref="EprRegisterEnrolManagementBe.WorkItems.Core.SlaClock.TargetDuration"/>
    /// by (an override that shrinks or leaves the target duration
    /// unchanged is not bounded by this value). Requests above this cap
    /// are rejected with HTTP 422. Defaults to 31.
    /// </summary>
    public int MaxExtensionDays { get; set; } = 31;
}
