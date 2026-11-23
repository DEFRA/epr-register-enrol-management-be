using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// The checks the operator backend's one-off corrections rely on to leave a
/// work item alone: RA-766's charge correction and RA-768's overseas-sites
/// removal.
/// </summary>
internal static class ReAccreditationCorrectionGuards
{
    private const string PaymentDateField = "paymentDate";

    /// <summary>
    /// Whether the application has a decided outcome: the template's terminal
    /// states (approved, rejected, withdrawn), plus the two pending-contact
    /// states. Those two are not terminal, but the outcome is already decided.
    /// The pending-contact states come from <see cref="StandardConfirmContactGuard"/>,
    /// so they stay in step with it. The operator backend keeps its own copy of
    /// the five ids for its dry runs, which never call this service
    /// (<c>ChargeCorrectionMigration.ClosedWorkItemStates</c> for RA-766, and the
    /// RA-768 overseas-sites migration). A change here needs a change there too.
    /// </summary>
    internal static bool IsDecided(IWorkItemTemplate template, string stateId) =>
        TerminalStates.Find(template, stateId) is not null
        || StandardConfirmContactGuard.ForPendingState(stateId) is not null;

    /// <summary>
    /// Whether duly making has been completed. Duly making is what stamps
    /// <c>payload.paymentDate</c>, so a non-null value marks it.
    /// </summary>
    internal static bool IsDulyMade(WorkItem workItem) =>
        workItem.Payload is not null
        && workItem.Payload.TryGetValue(PaymentDateField, out var value)
        && !value.IsBsonNull;
}
