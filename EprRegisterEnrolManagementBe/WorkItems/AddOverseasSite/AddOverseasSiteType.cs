using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.WorkItems.AddOverseasSite;

/// <summary>
/// RA-739: the case type for an operator's request to add an overseas
/// reprocessing site (ORS) to an accreditation. It is an in-year change
/// (<see cref="WorkItemCategories.InYearChange"/>) with the code <c>ORS</c>,
/// so each work item gets an <c>IYC-ORS-{yyyy}-{sequence}</c> reference. A
/// submission holds one or more sites and is split into one work item per
/// site, all sharing one <c>SUB-{yyyy}-{sequence}</c> submission reference.
/// It follows the shared <see cref="StandardWorkflow"/>. Its action endpoints
/// (duly make, query, determination, confirm contact) arrive with RA-495.
/// </summary>
internal sealed class AddOverseasSiteType : IWorkItemType
{
    public const string Id = "add-overseas-site";

    public string TypeId => Id;
    public string DisplayName => "New ORS accreditation";
    public string TemplateVersion => "v1";

    public string? Category => WorkItemCategories.InYearChange;
    public string? InYearChangeCode => "ORS";
    public bool UsesSubmissionReference => true;
    public WorkItemWorkflow Workflow => StandardWorkflow.Definition;
    public WorkItemState InitialState => Workflow.InitialState;
    public IReadOnlyCollection<WorkItemState> States => Workflow.States;
    public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;
}
