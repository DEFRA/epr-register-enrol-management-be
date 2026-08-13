using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Re-accreditation work item type (RA-98). Reference module that proves the
/// framework's "one folder + one registration line" promise. The states /
/// transitions declared here follow the workflow diagram referenced
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
    // v11 (RA-316): duly making is an explicit regulator action again. The
    // duly-make transition is REINSTATED (it was removed at v5 in favour of an
    // auto-transition hook), the two 'submitted' tasks that drove that hook are
    // removed, and the hook itself is deleted. Unlike v5's version, this one is
    // CallerInvocable: false — it is reachable only through
    // POST /work-items/re-accreditation/{id}/duly-make, which captures the
    // payment date the SLA clock is anchored to. Items snapshotted before v11
    // still carry the submitted-state tasks and no duly-make transition, so
    // they have no way to be duly made until
    // ReAccreditationDulyMakeSnapshotMigration patches their frozen snapshot.
    // v12 (RA-410): the task framework is gone. Every per-state checklist and
    // every "all tasks complete" gate is deleted, so the transitions that used
    // to be gated (payment-received, submit-for-decision) now simply succeed.
    // submit-for-decision and reject additionally become CallerInvocable:
    // false — a decision is now one call to
    // POST /work-items/re-accreditation/{id}/decision, which performs both
    // hops server-side. Items snapshotted before v12 keep the v11 action set
    // until ReAccreditationDecisionSnapshotMigration patches their frozen
    // snapshot.
    public string TemplateVersion => "v12";
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
        // RA-316: duly making. Handled exclusively by
        // ReAccreditationDulyMakingService via
        // POST /work-items/re-accreditation/{id}/duly-make.
        //
        // CallerInvocable is false for the same reason approve is not
        // registered at all: the bespoke endpoint carries side effects the
        // generic engine cannot perform — it anchors the 12-week SLA clock to
        // the regulator-entered payment date (RA-316 AC06), not to now. A
        // caller reaching this through /work-items/{id}/actions/duly-make would
        // move the item to duly-made with no payment date and therefore no
        // clock, silently defeating the SLA.
        new WorkItemTransition(
            "duly-make",
            "Duly make",
            s_submitted.Id,
            s_dulyMade.Id,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "payment-received",
            "Payment received",
            s_dulyMade.Id,
            s_assessmentInProgress.Id
        ),
        // SLA extension is a self-loop on assessment-in-progress so an
        // assessor can record an extension at any time during assessment.
        new WorkItemTransition(
            "sla-extend",
            "Extend SLA",
            s_assessmentInProgress.Id,
            s_assessmentInProgress.Id
        ),
        // RA-410: 'awaiting-decision' survives as an internal hop, but no
        // caseworker clicks through it any more. Recording a decision is a
        // single call to POST /work-items/re-accreditation/{id}/decision,
        // which applies this transition and then the outcome server-side, so
        // a failure between the two cannot strand an application in
        // 'awaiting-decision' with no way forward.
        //
        // CallerInvocable is false to make that the only route. Left
        // invocable, the generic action endpoint would keep offering a
        // "Submit for decision" button whose only effect is to park an
        // application in the intermediate state this story exists to hide.
        new WorkItemTransition(
            "submit-for-decision",
            "Submit for decision",
            s_assessmentInProgress.Id,
            s_awaitingDecision.Id,
            CallerInvocable: false
        ),
        // RA-132: approve is handled exclusively by ReAccreditationApprovalService
        // via POST /work-items/re-accreditation/{id}/approve. The transition is NOT
        // registered here so the generic engine rejects any attempt to call
        // /work-items/{id}/actions/approve, preventing a caller from bypassing the
        // bespoke side-effects (accreditation id issuance, SLA clock stop, queued
        // publishing job).
        //
        // RA-410: reject is now CallerInvocable: false for the same reason as
        // submit-for-decision above. Both halves of a decision are driven by
        // ReAccreditationLogDecisionService through the single /decision
        // endpoint, so the pair either both happen or neither does. The action
        // id stays 'reject' and the target state stays 'rejected' — the
        // regulator-facing "Refused" wording is a frontend label only, and
        // renaming either would break every notification template and stored
        // audit entry that names them.
        new WorkItemTransition(
            "reject",
            "Reject",
            s_awaitingDecision.Id,
            s_rejected.Id,
            CallerInvocable: false
        ),
        // RA-211 / RA-291: a case worker can query an application from any
        // pre-decision state when they need clarification before proceeding.
        // There is deliberately no transition
        // out of 'queried' back to 'queried': an application awaiting a
        // response cannot be queried again.
        new WorkItemTransition(
            "query-during-duly-making",
            "Query",
            s_submitted.Id,
            s_queried.Id
        ),
        new WorkItemTransition(
            "query-during-duly-made",
            "Query",
            s_dulyMade.Id,
            s_queried.Id
        ),
        new WorkItemTransition(
            "query-during-assessment",
            "Query",
            s_assessmentInProgress.Id,
            s_queried.Id
        ),
        new WorkItemTransition(
            "query-during-decision",
            "Query",
            s_awaitingDecision.Id,
            s_queried.Id
        ),
        // RA-311/MBE-1: the inverse of the four query-during-* transitions
        // above, one per originating state, so a resubmitted application
        // moves out of 'queried'. Which one applies is resolved server-side
        // (ReAccreditationResumeService) from the work item's own
        // 'application-queried' audit history, never chosen by the caller.
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
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "resume-during-duly-made",
            "Resume",
            s_queried.Id,
            s_updated.Id,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "resume-during-assessment",
            "Resume",
            s_queried.Id,
            s_updated.Id,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "resume-during-decision",
            "Resume",
            s_queried.Id,
            s_updated.Id,
            CallerInvocable: false
        ),
        // RA-337: the inverse of the four resume-during-* transitions above,
        // one per originating state, so a work item a caseworker has
        // finished reviewing in 'updated' moves on to wherever it was
        // queried from. Which one applies is resolved server-side
        // (ReAccreditationContinueReviewService) from the work item's own
        // resume-during-* audit entry, never chosen by the caller.
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
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "continue-review-during-duly-made",
            "Continue review",
            s_updated.Id,
            s_dulyMade.Id,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "continue-review-during-assessment",
            "Continue review",
            s_updated.Id,
            s_assessmentInProgress.Id,
            CallerInvocable: false
        ),
        new WorkItemTransition(
            "continue-review-during-decision",
            "Continue review",
            s_updated.Id,
            s_awaitingDecision.Id,
            CallerInvocable: false
        ),
        // Withdrawal is always available before a decision is recorded, so an
        // organisation can withdraw at any point during review.
        new WorkItemTransition(
            "withdraw",
            "Withdraw",
            s_submitted.Id,
            s_withdrawn.Id
        ),
        new WorkItemTransition(
            "withdraw-during-duly-made",
            "Withdraw",
            s_dulyMade.Id,
            s_withdrawn.Id
        ),
        new WorkItemTransition(
            "withdraw-during-assessment",
            "Withdraw",
            s_assessmentInProgress.Id,
            s_withdrawn.Id
        ),
        new WorkItemTransition(
            "withdraw-during-decision",
            "Withdraw",
            s_awaitingDecision.Id,
            s_withdrawn.Id
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
            s_withdrawn.Id
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
            s_withdrawn.Id
        ),
    ];

}
