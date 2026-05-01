using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Re-accreditation work item type (RA-98). Reference module that proves the
/// framework's "one folder + one registration line" promise. The states /
/// transitions / tasks declared here are placeholders for the PoC and follow
/// the workflow diagram referenced in RA-85; the shape is intentionally
/// declarative so a reader can grasp the lifecycle without reading code.
/// </summary>
internal sealed class ReAccreditationType : IWorkItemType
{
    public const string Id = "re-accreditation";

    private static readonly WorkItemState s_submitted = new("submitted", "Submitted");
    private static readonly WorkItemState s_assessmentInProgress =
        new("assessment-in-progress", "Assessment in progress");
    private static readonly WorkItemState s_awaitingDecision =
        new("awaiting-decision", "Awaiting decision");
    private static readonly WorkItemState s_approved = new("approved", "Approved", IsTerminal: true);
    private static readonly WorkItemState s_rejected = new("rejected", "Rejected", IsTerminal: true);
    private static readonly WorkItemState s_withdrawn = new("withdrawn", "Withdrawn", IsTerminal: true);

    /// <summary>
    /// Role names that authorise approving or rejecting a re-accreditation.
    /// Segregation of duties: an assessor who completes tasks should not be
    /// the same user who records the final decision. Holding this role grants
    /// permission to invoke the approve / reject transitions.
    /// </summary>
    public const string DecisionMakerRole = "reaccreditation-decision-maker";

    private static readonly IReadOnlyCollection<string> s_decisionMakerRoles = new[] { DecisionMakerRole };

    private static readonly Dictionary<string, IReadOnlyCollection<WorkItemTask>> s_tasksByState =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [s_submitted.Id] =
            [
                new WorkItemTask("verify-organisation-details", "Verify organisation details"),
                new WorkItemTask("confirm-registration-fee-paid", "Confirm registration fee paid")
            ],
            [s_assessmentInProgress.Id] =
            [
                new WorkItemTask("review-compliance-history", "Review compliance history"),
                new WorkItemTask("assess-technical-capacity", "Assess technical capacity"),
                new WorkItemTask("assess-financial-capacity", "Assess financial capacity")
            ],
            [s_awaitingDecision.Id] =
            [
                new WorkItemTask("record-decision-rationale", "Record decision rationale")
            ]
        };

    public string TypeId => Id;
    public string DisplayName => "Re-accreditation";
    // epr-gl6: per-state task contract gained a richer WorkItemTaskStatus
    // (NotStarted/InProgress/Blocked/Completed) alongside the legacy
    // binary view, so bump the template version even though the schema
    // (states / transitions / task lists) is otherwise unchanged.
    public string TemplateVersion => "v2";
    public WorkItemState InitialState => s_submitted;

    public IReadOnlyCollection<WorkItemState> States { get; } =
    [
        s_submitted,
        s_assessmentInProgress,
        s_awaitingDecision,
        s_approved,
        s_rejected,
        s_withdrawn
    ];

    public IReadOnlyCollection<WorkItemTransition> Transitions { get; } =
    [
        new WorkItemTransition(
            "start-assessment", "Start assessment",
            s_submitted.Id, s_assessmentInProgress.Id),
        new WorkItemTransition(
            "submit-for-decision", "Submit for decision",
            s_assessmentInProgress.Id, s_awaitingDecision.Id),
        new WorkItemTransition(
            "approve", "Approve",
            s_awaitingDecision.Id, s_approved.Id,
            RequiredRoles: s_decisionMakerRoles),
        new WorkItemTransition(
            "reject", "Reject",
            s_awaitingDecision.Id, s_rejected.Id,
            RequiredRoles: s_decisionMakerRoles),

        // Withdrawal is always available before a decision is recorded; it
        // bypasses the "all tasks complete" gate so an organisation can
        // withdraw at any point without an assessor having to first tick
        // every box.
        new WorkItemTransition(
            "withdraw", "Withdraw",
            s_submitted.Id, s_withdrawn.Id, RequiresAllTasksComplete: false),
        new WorkItemTransition(
            "withdraw-during-assessment", "Withdraw",
            s_assessmentInProgress.Id, s_withdrawn.Id, RequiresAllTasksComplete: false),
        new WorkItemTransition(
            "withdraw-during-decision", "Withdraw",
            s_awaitingDecision.Id, s_withdrawn.Id, RequiresAllTasksComplete: false)
    ];

    public IReadOnlyCollection<WorkItemTask> GetTasksForState(string stateId) =>
        s_tasksByState.TryGetValue(stateId, out var tasks) ? tasks : Array.Empty<WorkItemTask>();
}