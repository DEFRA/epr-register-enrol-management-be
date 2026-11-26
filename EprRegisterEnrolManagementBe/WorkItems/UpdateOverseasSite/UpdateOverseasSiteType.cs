using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;

/// <summary>
/// RA-682: the case type for an operator's request to change the details of
/// an overseas reprocessing site (ORS) that is already on their accreditation,
/// including adding, changing and removing its interim sites. It mirrors
/// <see cref="AddOverseasSite.AddOverseasSiteType"/>: an in-year change
/// (<see cref="WorkItemCategories.InYearChange"/>) sharing the <c>ORS</c> code,
/// so its work items take <c>IYC-ORS-{yyyy}-{sequence}</c> references from the
/// same series as add-ORS. A submission is split into one work item per site,
/// and the type follows the same Standard workflow with the withdraw*
/// transitions not caller-invocable. Unlike add-ORS it carries no charge, so a
/// submission needs no payment reference (AC05).
/// </summary>
internal sealed class UpdateOverseasSiteType : IWorkItemType
{
    public const string Id = "update-overseas-site";

    internal static readonly WorkItemWorkflow WorkflowDefinition = StandardWorkflow.Definition with
    {
        Id = "standard-update-overseas-site",
        Transitions = StandardWorkflow
            .Definition.Transitions.Select(t => IsWithdraw(t) ? t with { CallerInvocable = false } : t)
            .ToList(),
    };

    public string TypeId => Id;
    public string DisplayName => "ORS update";

    public string? Category => WorkItemCategories.InYearChange;

    /// <summary>The same code as add-ORS (user, 2026-11-26): one reference series for ORS changes.</summary>
    public string? InYearChangeCode => "ORS";

    public bool SplitsSubmissionPerSite => true;

    /// <summary>RA-682 AC05: no charge, so nothing is paid and there is no payment reference.</summary>
    public bool RequiresPaymentReference => false;

    public WorkItemWorkflow Workflow => WorkflowDefinition;
    public WorkItemState InitialState => Workflow.InitialState;
    public IReadOnlyCollection<WorkItemState> States => Workflow.States;
    public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;

    // As add-ORS: a withdraw is any move onto the Withdrawn state, and the push
    // hook never pushes one, so the operator journey must not be able to take it.
    private static bool IsWithdraw(WorkItemTransition transition) =>
        transition.ToStateId == StandardWorkflow.WithdrawnStateId;
}
