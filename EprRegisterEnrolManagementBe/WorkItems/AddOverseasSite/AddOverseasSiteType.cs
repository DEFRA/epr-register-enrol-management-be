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
/// It follows the shared <see cref="StandardWorkflow"/>, and its lifecycle
/// actions run through the shared Standard-lifecycle routes (RA-495, see
/// <see cref="AddOverseasSiteLifecycleStrategy"/>). Query and withdraw stay
/// re-accreditation-only: the query transitions are already
/// <c>CallerInvocable: false</c> in the Standard workflow, and this type's own
/// <see cref="Workflow"/> makes every <c>withdraw*</c> declaration
/// <c>CallerInvocable: false</c> too (v2).
/// </summary>
internal sealed class AddOverseasSiteType : IWorkItemType
{
    public const string Id = "add-overseas-site";

    /// <summary>
    /// RA-495 review: the Standard workflow with every <c>withdraw*</c>
    /// declaration made <c>CallerInvocable: false</c>. Add-ORS has no withdraw
    /// service, so this keeps <c>POST /work-items/{id}/actions/withdraw*</c>
    /// from withdrawing an add-ORS item: the status push hook never pushes a
    /// move onto <c>withdrawn</c>, so the operator journey would never hear of
    /// it and the site would stay locked there. States, targets and every
    /// other transition are the Standard workflow's, so the shared lifecycle
    /// services and the push hook's exclusions work unchanged.
    /// <see cref="StandardWorkflow"/> itself is left alone because
    /// re-accreditation withdraws through it.
    /// </summary>
    internal static readonly WorkItemWorkflow WorkflowDefinition = StandardWorkflow.Definition with
    {
        Id = "standard-add-overseas-site",
        Transitions = StandardWorkflow
            .Definition.Transitions.Select(t => IsWithdraw(t) ? t with { CallerInvocable = false } : t)
            .ToList(),
    };

    public string TypeId => Id;
    public string DisplayName => "New ORS accreditation";

    // v1 (RA-739): the Standard workflow as it stood (re-accreditation's v19
    // rules), with the withdraw* transitions caller-invocable.
    // v2 (RA-495 review): every withdraw* declaration became CallerInvocable:
    // false (see WorkflowDefinition). No state or target changed. Items
    // snapshotted under v1 keep them caller-invocable until
    // AddOverseasSiteWithdrawNotCallerInvocableSnapshotMigration patches
    // their frozen snapshot.
    public string TemplateVersion => "v2";

    public string? Category => WorkItemCategories.InYearChange;
    public string? InYearChangeCode => "ORS";
    public bool SplitsSubmissionPerSite => true;
    public WorkItemWorkflow Workflow => WorkflowDefinition;
    public WorkItemState InitialState => Workflow.InitialState;
    public IReadOnlyCollection<WorkItemState> States => Workflow.States;
    public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;

    private static bool IsWithdraw(WorkItemTransition transition) =>
        transition.ActionId.StartsWith("withdraw", StringComparison.OrdinalIgnoreCase);
}
