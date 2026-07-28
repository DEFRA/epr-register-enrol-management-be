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

    // RA-324 (AC06): state DisplayNames align with the prototype "Applications"
    // design. Only the labels change here — the state ids are the wire contract
    // the management-fe and mgmt-tests key off and MUST stay exactly as they
    // are. Per literal AC06 this deliberately makes 'assessment-in-progress'
    // and the existing 'updated' state both display "Updated"; that clash is
    // accepted, not reconciled.
    private static readonly WorkItemState s_submitted = new("submitted", "Not started");
    private static readonly WorkItemState s_dulyMade = new("duly-made", "Duly made");
    private static readonly WorkItemState s_assessmentInProgress = new(
        "assessment-in-progress",
        "Updated"
    );
    private static readonly WorkItemState s_awaitingDecision = new(
        "awaiting-decision",
        "Awaiting decision"
    );

    // RA-211: not terminal — a queried application is paused pending regulator
    // clarification, not a closed outcome like approved/rejected/withdrawn.
    // RA-311/MBE-1: the resume-during-* transitions below are the way out,
    // one per originating state.
    private static readonly WorkItemState s_queried = new("queried", "Queried");

    // RA-337: not terminal — a resubmitted-but-not-yet-reviewed application.
    // resume-during-* lands here (instead of jumping straight back to the
    // originating state) so CM has a distinct status to show a caseworker
    // that a query response has arrived. continue-review-during-* is the way
    // out, one per originating state, resolved server-side by
    // ReAccreditationContinueReviewService from the resume-during-* action
    // that put the item here.
    private static readonly WorkItemState s_updated = new("updated", "Updated");
    private static readonly WorkItemState s_approved = new(
        "approved",
        "Granted",
        IsTerminal: true
    );
    private static readonly WorkItemState s_rejected = new(
        "rejected",
        "Refused",
        IsTerminal: true
    );
    private static readonly WorkItemState s_withdrawn = new(
        "withdrawn",
        "Withdrawn",
        IsTerminal: true
    );

    private static readonly Dictionary<string, IReadOnlyCollection<WorkItemTask>> s_tasksByState =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [s_submitted.Id] =
            [
                new WorkItemTask("verify-organisation-details", "Verify organisation details"),
                new WorkItemTask(
                    "confirm-application-completeness",
                    "Confirm application is duly made"
                ),
            ],
            [s_dulyMade.Id] =
            [
                new WorkItemTask("confirm-registration-fee-paid", "Confirm registration fee paid"),
            ],
            [s_assessmentInProgress.Id] =
            [
                new WorkItemTask("review-compliance-history", "Review compliance history"),
                new WorkItemTask("assess-technical-capacity", "Assess technical capacity"),
                new WorkItemTask("assess-financial-capacity", "Assess financial capacity"),
            ],
            [s_awaitingDecision.Id] =
            [
                new WorkItemTask("record-decision-rationale", "Record decision rationale"),
            ],
        };

    public string TypeId => Id;
    public string DisplayName => "Re-accreditation";

    // v5: removed duly-make action — the submitted→duly-made transition is now
    // triggered automatically by ReAccreditationDulyMadeHook when all
    // submitted-state tasks are completed.
    // v6 (RA-291): added query-during-duly-making (submitted → queried) and
    // query-during-duly-made (duly-made → queried). Items snapshotted at v5
    // keep the v5 action set, so only work items submitted from this version
    // onwards can be queried before assessment starts.
    // v7 (RA-311/MBE-1): added the four resume-during-* transitions out of
    // 'queried', one per originating state, so a resubmitted application can
    // return to the state it was queried from. Items snapshotted before v7
    // have no way out of 'queried' until ReAccreditationResumeSnapshotMigration
    // patches their frozen snapshot.
    // v8 (RA-337): CM previously showed the pre-query state's label
    // immediately after a resume, with no signal that a response had
    // arrived — the resume-during-* transitions now land on a new
    // non-terminal 'updated' state instead of jumping straight back to the
    // originating state. Four new continue-review-during-* transitions, one
    // per originating state, carry a work item on from 'updated' once a
    // caseworker has reviewed the response; which one applies is resolved
    // server-side by ReAccreditationContinueReviewService from the
    // resume-during-* action that put the item in 'updated', never chosen
    // by the caller. Items snapshotted before v8 have resume-during-*
    // transitions that still jump straight to the originating state until
    // ReAccreditationUpdatedStateSnapshotMigration patches their frozen
    // snapshot.
    // v9 (RA-252): added withdraw-during-query (queried -> withdrawn) so an
    // operator can withdraw an application that is currently awaiting a
    // query response, not just the four pre-decision states already
    // covered by withdraw/withdraw-during-*. Items snapshotted before v9
    // have no way to reach 'withdrawn' from 'queried' until
    // ReAccreditationWithdrawQuerySnapshotMigration patches their frozen
    // snapshot.
    // v10 (RA-252): added withdraw-during-updated (updated -> withdrawn) so
    // an operator can withdraw an application that is currently in
    // 'updated' — a query response has arrived but a caseworker has not
    // yet actioned continue-review-during-* to carry it back into review.
    // Without this, an operator whose application sits in 'updated' had no
    // way to withdraw at all, even though RA-252's business rule permits
    // withdrawal at any point before a final decision. Items snapshotted
    // before v10 have no way to reach 'withdrawn' from 'updated' until
    // ReAccreditationWithdrawUpdatedSnapshotMigration patches their frozen
    // snapshot.
    public string TemplateVersion => "v10";
    public WorkItemState InitialState => s_submitted;

    public IReadOnlyCollection<WorkItemState> States { get; } =
    [
        s_submitted,
        s_dulyMade,
        s_assessmentInProgress,
        s_awaitingDecision,
        s_queried,
        s_updated,
        s_approved,
        s_rejected,
        s_withdrawn,
    ];

    public IReadOnlyCollection<WorkItemTransition> Transitions { get; } =
    [
        new WorkItemTransition(
            "payment-received",
            "Payment received",
            s_dulyMade.Id,
            s_assessmentInProgress.Id
        ),
        // SLA extension is a self-loop on assessment-in-progress; it
        // bypasses the "all tasks complete" gate so an assessor can
        // record an extension at any time during assessment.
        new WorkItemTransition(
            "sla-extend",
            "Extend SLA",
            s_assessmentInProgress.Id,
            s_assessmentInProgress.Id,
            RequiresAllTasksComplete: false
        ),
        new WorkItemTransition(
            "submit-for-decision",
            "Submit for decision",
            s_assessmentInProgress.Id,
            s_awaitingDecision.Id
        ),
        // RA-132: approve is handled exclusively by ReAccreditationApprovalService
        // via POST /work-items/re-accreditation/{id}/approve. The transition is NOT
        // registered here so the generic engine rejects any attempt to call
        // /work-items/{id}/actions/approve, preventing a caller from bypassing the
        // bespoke side-effects (accreditation id issuance, SLA clock stop, queued
        // publishing job). Reject still goes through awaiting-decision via the generic engine.
        new WorkItemTransition("reject", "Reject", s_awaitingDecision.Id, s_rejected.Id),
        // RA-211 / RA-291: a case worker can query an application from any
        // pre-decision state when they need clarification before proceeding.
        // Like sla-extend/withdraw, this bypasses the "all tasks complete"
        // gate — a query can be raised at any point during review, not just
        // once every task box is ticked. There is deliberately no transition
        // out of 'queried' back to 'queried': an application awaiting a
        // response cannot be queried again.
        new WorkItemTransition(
            "query-during-duly-making",
            "Query",
            s_submitted.Id,
            s_queried.Id,
            RequiresAllTasksComplete: false
        ),
        new WorkItemTransition(
            "query-during-duly-made",
            "Query",
            s_dulyMade.Id,
            s_queried.Id,
            RequiresAllTasksComplete: false
        ),
        new WorkItemTransition(
            "query-during-assessment",
            "Query",
            s_assessmentInProgress.Id,
            s_queried.Id,
            RequiresAllTasksComplete: false
        ),
        new WorkItemTransition(
            "query-during-decision",
            "Query",
            s_awaitingDecision.Id,
            s_queried.Id,
            RequiresAllTasksComplete: false
        ),
        // RA-311/MBE-1: the inverse of the four query-during-* transitions
        // above, one per originating state, so a resubmitted application
        // moves out of 'queried'. Which one applies is resolved server-side
        // (ReAccreditationResumeService) from the work item's own
        // 'application-queried' audit history, never chosen by the caller.
        // RequiresAllTasksComplete is false for the same reason as
        // query-during-*: task completeness for the target state is
        // re-evaluated there, not gated on the way in.
        // RA-337: these land on 'updated' rather than jumping straight back
        // to the originating state — see continue-review-during-* below.
        //
        // Security review (RA-311/MBE-1): CallerInvocable is false on all
        // four. Unlike query-during-*, these four transitions all share the
        // same FromStateId ('queried'), so the engine's normal from-state
        // guard cannot tell them apart — a caller who could invoke them
        // directly via the generic action endpoint could pick any of the
        // four target states regardless of which state the item was
        // actually queried from, bypassing ReAccreditationResumeService's
        // audit-history resolution and the validation/audit trail it
        // performs, and skipping intermediate states/tasks entirely.
        new WorkItemTransition(
            "resume-during-duly-making",
            "Resume",
            s_queried.Id,
            s_updated.Id,
            RequiresAllTasksComplete: false,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "resume-during-duly-made",
            "Resume",
            s_queried.Id,
            s_updated.Id,
            RequiresAllTasksComplete: false,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "resume-during-assessment",
            "Resume",
            s_queried.Id,
            s_updated.Id,
            RequiresAllTasksComplete: false,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "resume-during-decision",
            "Resume",
            s_queried.Id,
            s_updated.Id,
            RequiresAllTasksComplete: false,
            CallerInvocable: false
        ),
        // RA-337: the inverse of the four resume-during-* transitions above,
        // one per originating state, so a work item a caseworker has
        // finished reviewing in 'updated' moves on to wherever it was
        // queried from. Which one applies is resolved server-side
        // (ReAccreditationContinueReviewService) from the work item's own
        // resume-during-* audit entry, never chosen by the caller.
        // RequiresAllTasksComplete is false because 'updated' has no tasks
        // of its own — task completeness for the target state is
        // re-evaluated there, not gated on the way in.
        //
        // Security review (RA-311/MBE-1): CallerInvocable is false for the
        // same reason as resume-during-* above — all four share FromStateId
        // 'updated', so a directly-invoked caller choice would bypass
        // ReAccreditationContinueReviewService's audit-history resolution
        // and could send the item to the wrong (attacker-chosen) stage.
        new WorkItemTransition(
            "continue-review-during-duly-making",
            "Continue review",
            s_updated.Id,
            s_submitted.Id,
            RequiresAllTasksComplete: false,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "continue-review-during-duly-made",
            "Continue review",
            s_updated.Id,
            s_dulyMade.Id,
            RequiresAllTasksComplete: false,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "continue-review-during-assessment",
            "Continue review",
            s_updated.Id,
            s_assessmentInProgress.Id,
            RequiresAllTasksComplete: false,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "continue-review-during-decision",
            "Continue review",
            s_updated.Id,
            s_awaitingDecision.Id,
            RequiresAllTasksComplete: false,
            CallerInvocable: false
        ),
        // Withdrawal is always available before a decision is recorded; it
        // bypasses the "all tasks complete" gate so an organisation can
        // withdraw at any point without an assessor having to first tick
        // every box.
        new WorkItemTransition(
            "withdraw",
            "Withdraw",
            s_submitted.Id,
            s_withdrawn.Id,
            RequiresAllTasksComplete: false
        ),
        new WorkItemTransition(
            "withdraw-during-duly-made",
            "Withdraw",
            s_dulyMade.Id,
            s_withdrawn.Id,
            RequiresAllTasksComplete: false
        ),
        new WorkItemTransition(
            "withdraw-during-assessment",
            "Withdraw",
            s_assessmentInProgress.Id,
            s_withdrawn.Id,
            RequiresAllTasksComplete: false
        ),
        new WorkItemTransition(
            "withdraw-during-decision",
            "Withdraw",
            s_awaitingDecision.Id,
            s_withdrawn.Id,
            RequiresAllTasksComplete: false
        ),
        // RA-252: an operator can withdraw an application awaiting a query
        // response too — unlike resume-during-*/continue-review-during-*
        // there is only one possible target state (withdrawn) from
        // 'queried', so this is CallerInvocable (default) with no ambiguity
        // for the engine's from-state guard to resolve.
        new WorkItemTransition(
            "withdraw-during-query",
            "Withdraw",
            s_queried.Id,
            s_withdrawn.Id,
            RequiresAllTasksComplete: false
        ),
        // RA-252: an operator can also withdraw an application sitting in
        // 'updated' — a query response has arrived but a caseworker has not
        // yet reviewed it via continue-review-during-*. As with
        // withdraw-during-query, there is only one possible target state
        // (withdrawn) from 'updated', so this is CallerInvocable (default)
        // with no ambiguity for the engine's from-state guard to resolve.
        new WorkItemTransition(
            "withdraw-during-updated",
            "Withdraw",
            s_updated.Id,
            s_withdrawn.Id,
            RequiresAllTasksComplete: false
        ),
    ];

    public IReadOnlyCollection<WorkItemTask> GetTasksForState(string stateId) =>
        s_tasksByState.TryGetValue(stateId, out var tasks) ? tasks : Array.Empty<WorkItemTask>();
}
