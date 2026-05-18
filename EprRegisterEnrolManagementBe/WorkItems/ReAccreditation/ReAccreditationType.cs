using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Re-accreditation work item type (RA-98). Reference module that proves the
/// framework's "one folder + one registration line" promise. The states /
/// transitions / tasks declared here follow the workflow diagram referenced
/// in RA-85; the shape is intentionally declarative so a reader can grasp
/// the lifecycle without reading code.
/// </summary>
internal sealed class ReAccreditationType : IWorkItemType
{
    public const string Id = "re-accreditation";

    private static readonly WorkItemState s_submitted = new("submitted", "Submitted");
    private static readonly WorkItemState s_dulyMade = new("duly-made", "Duly made");
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
                new WorkItemTask("confirm-application-completeness", "Confirm application is duly made")
            ],
            [s_dulyMade.Id] =
            [
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
    // RA-123: introduced duly-made state plus duly-make / payment-received /
    // sla-extend transitions, and added OperatorEmail to the payload to
    // drive GOV.UK Notify sends. Bump template version so in-flight v2
    // items keep rendering against their captured snapshot.
    // RA-132: approval is handled exclusively by ReAccreditationApprovalService
    // (POST /work-items/re-accreditation/{id}/approve). That service stamps
    // AccreditationId, AccreditationStartDate and SlaClock on the payload.
    // v3 items must keep rendering against their captured snapshot so bump to v4.
    public string TemplateVersion => "v4";
    public WorkItemState InitialState => s_submitted;

    public IReadOnlyCollection<WorkItemState> States { get; } =
    [
        s_submitted,
        s_dulyMade,
        s_assessmentInProgress,
        s_awaitingDecision,
        s_approved,
        s_rejected,
        s_withdrawn
    ];

    public IReadOnlyCollection<WorkItemTransition> Transitions { get; } =
    [
        new WorkItemTransition(
            "duly-make", "Mark as duly made",
            s_submitted.Id, s_dulyMade.Id),
        new WorkItemTransition(
            "payment-received", "Record payment received",
            s_dulyMade.Id, s_assessmentInProgress.Id),

        // SLA extension is a self-loop on assessment-in-progress; it
        // bypasses the "all tasks complete" gate so an assessor can
        // record an extension at any time during assessment.
        new WorkItemTransition(
            "sla-extend", "Extend SLA",
            s_assessmentInProgress.Id, s_assessmentInProgress.Id,
            RequiresAllTasksComplete: false),

        new WorkItemTransition(
            "submit-for-decision", "Submit for decision",
            s_assessmentInProgress.Id, s_awaitingDecision.Id),
        // RA-132: approve is handled exclusively by ReAccreditationApprovalService
        // via POST /work-items/re-accreditation/{id}/approve. The transition is NOT
        // registered here so the generic engine rejects any attempt to call
        // /work-items/{id}/actions/approve, preventing a caller from bypassing the
        // bespoke side-effects (accreditation id issuance, SLA clock stop, queued
        // publishing job). Reject still goes through awaiting-decision via the generic engine.
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
            "withdraw-during-duly-made", "Withdraw",
            s_dulyMade.Id, s_withdrawn.Id, RequiresAllTasksComplete: false),
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
