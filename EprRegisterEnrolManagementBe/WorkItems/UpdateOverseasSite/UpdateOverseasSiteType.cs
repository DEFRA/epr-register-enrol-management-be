using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.WorkItems.UpdateOverseasSite;

/// <summary>
/// RA-682: the case type for an operator's request to change the details of
/// an overseas reprocessing site (ORS) that is already on their accreditation,
/// including adding, changing and removing its interim sites. It mirrors
/// the add-ORS type (<c>add-overseas-site</c>): an in-year change
/// (<see cref="WorkItemCategories.InYearChange"/>) sharing the <c>ORS</c> code,
/// so its work items take <c>IYC-ORS-{yyyy}-{sequence}</c> references from the
/// same series as add-ORS. A submission is split into one work item per site,
/// and the type follows the same workflow rules as add-ORS
/// (<see cref="StandardWorkflow.WithdrawNotCallerInvocable"/>). Unlike
/// add-ORS it carries no charge, so a submission needs no payment reference
/// (AC05).
/// </summary>
internal sealed class UpdateOverseasSiteType : IWorkItemType
{
    public const string Id = "update-overseas-site";

    private static readonly WorkItemWorkflow s_workflow = StandardWorkflow.WithdrawNotCallerInvocable(
        "standard-update-overseas-site"
    );

    public string TypeId => Id;
    public string DisplayName => "ORS update";

    public string? Category => WorkItemCategories.InYearChange;

    /// <summary>
    /// The same code as add-ORS, decided on RA-682: one reference series for
    /// ORS changes (see docs/work-items.md).
    /// </summary>
    public string? InYearChangeCode => "ORS";

    public bool SplitsSubmissionPerSite => true;

    /// <summary>RA-682 AC05: no charge, so nothing is paid and there is no payment reference.</summary>
    public IReadOnlyCollection<string> RequiredSubmissionFields => SubmissionPayload.UnchargedRequiredFields;

    public WorkItemWorkflow Workflow => s_workflow;
    public WorkItemState InitialState => Workflow.InitialState;
    public IReadOnlyCollection<WorkItemState> States => Workflow.States;
    public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;
}
