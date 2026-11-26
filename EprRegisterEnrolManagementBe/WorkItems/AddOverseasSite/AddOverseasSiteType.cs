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
/// <see cref="AddOverseasSiteLifecycleStrategy"/>). Withdraw stays
/// re-accreditation-only: this type's own <see cref="Workflow"/> makes every
/// <c>withdraw*</c> declaration <c>CallerInvocable: false</c> (v2).
///
/// RA-698 (v3): a regulator can query an add-ORS item and the operator can
/// answer it. The query-during-*, resume-during-* and continue-review-during-*
/// transitions are the Standard workflow's and stay
/// <c>CallerInvocable: false</c>: they are reachable only through this
/// module's bespoke services (<see cref="AddOverseasSiteQueryService"/>,
/// <see cref="AddOverseasSiteResumeService"/>,
/// <see cref="AddOverseasSiteContinueReviewService"/>), which resolve the
/// right one server-side, stamp and audit the query, and apply the response.
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
    // v3 (RA-698): the query, resume and continue-review transitions are
    // wired to bespoke endpoints. The declarations themselves are unchanged
    // (all CallerInvocable: false), so the bump marks the rules and
    // AddOverseasSiteQuerySnapshotMigration brings v1/v2 snapshots onto them,
    // adding any of those declarations a snapshot lacks.
    public string TemplateVersion => "v3";

    public string? Category => WorkItemCategories.InYearChange;
    public string? InYearChangeCode => "ORS";
    public bool SplitsSubmissionPerSite => true;
    public WorkItemWorkflow Workflow => WorkflowDefinition;
    public WorkItemState InitialState => Workflow.InitialState;
    public IReadOnlyCollection<WorkItemState> States => Workflow.States;
    public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;

    // A withdraw is any move onto the Withdrawn state, not an action-id
    // prefix: the push hook never pushes a move onto withdrawn, which is why
    // add-ORS must not take one.
    private static bool IsWithdraw(WorkItemTransition transition) =>
        transition.ToStateId == StandardWorkflow.WithdrawnStateId;
}
