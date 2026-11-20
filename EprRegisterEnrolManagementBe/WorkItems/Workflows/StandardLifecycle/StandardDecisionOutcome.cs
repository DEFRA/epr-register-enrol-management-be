namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: the determination a caseworker records on a Standard-workflow
/// work item. A closed two-value set: the caller picks an outcome, never a
/// destination. <see cref="Rejected"/> matches the <c>rejected</c> state id,
/// not the "Refused" label.
/// </summary>
internal enum StandardDecisionOutcome
{
    Approved,
    Rejected,
}
