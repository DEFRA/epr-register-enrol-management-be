namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;

/// <summary>
/// RA-132: SLA clock state for a re-accreditation work item. Stamped on the
/// payload when a decision-maker approves so reporting can see when the
/// regulator's clock stopped on the application.
/// </summary>
/// <param name="StoppedAt">UTC instant at which the SLA clock stopped, or
/// <c>null</c> when the clock is still running.</param>
public sealed record SlaClock(DateTimeOffset? StoppedAt);
