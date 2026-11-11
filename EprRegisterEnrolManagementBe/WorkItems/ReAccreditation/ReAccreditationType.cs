using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.Workflows;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// Re-accreditation work item type (RA-98). Reference module that proves the
/// framework's "one folder + one registration line" promise.
///
/// RA-739: this is re-accreditation's case type configuration — its label,
/// category (none), in-year change code (none), submission reference use
/// (none) and workflow. Its states and transitions are the shared
/// <see cref="StandardWorkflow"/>, which follows the workflow diagram
/// referenced in RA-85. The state and action id constants below are kept so
/// re-accreditation's services can keep naming them from their own type.
/// </summary>
internal sealed class ReAccreditationType : IWorkItemType
{
    public const string Id = "re-accreditation";

    // Re-accreditation's names for Standard workflow ids (documented on
    // StandardWorkflow), kept so its services read naturally.
    public const string ApprovedPendingContactStateId = StandardWorkflow.ApprovedPendingContactStateId;
    public const string MakeDeterminationApprovedActionId = StandardWorkflow.MakeDeterminationApprovedActionId;
    public const string MakeDeterminationApprovedDisplayName = StandardWorkflow.MakeDeterminationApprovedDisplayName;
    public const string ConfirmContactApprovedActionId = StandardWorkflow.ConfirmContactApprovedActionId;
    public const string ApprovedStateId = StandardWorkflow.ApprovedStateId;
    public const string ConfirmContactDisplayName = StandardWorkflow.ConfirmContactDisplayName;
    public const string RejectedPendingContactStateId = StandardWorkflow.RejectedPendingContactStateId;
    public const string MakeDeterminationRejectedActionId = StandardWorkflow.MakeDeterminationRejectedActionId;
    public const string MakeDeterminationRejectedDisplayName = StandardWorkflow.MakeDeterminationRejectedDisplayName;
    public const string ConfirmContactRejectedActionId = StandardWorkflow.ConfirmContactRejectedActionId;
    public const string RejectedStateId = StandardWorkflow.RejectedStateId;
    public const string AssessmentStartedStateId = StandardWorkflow.AssessmentStartedStateId;
    public const string WithdrawnStateId = StandardWorkflow.WithdrawnStateId;

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
    // v8 (RA-337): the Case Management service previously showed the pre-query state's label
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
    // v13 (RA-351): added a second sla-extend self-loop, on 'queried'
    // (queried -> queried), so the regulator can Extend/Override the SLA of
    // a queried application whose clock is at risk — previously the only
    // sla-extend transition was the assessment-in-progress self-loop, so
    // queried offered no SLA option. Items snapshotted before v13 have no
    // sla-extend transition from 'queried' until
    // ReAccreditationSlaExtendQuerySnapshotMigration patches their frozen
    // snapshot.
    // v14 (RA-523): retargeted resume-during-duly-made from the 'updated'
    // waypoint (queried -> updated) to assessment (queried ->
    // assessment-in-progress). An application queried AFTER it was duly made
    // was returning to the regulator as "Duly made" a second time via
    // continue-review-during-duly-made; it now arrives decision-ready with no
    // forward button, because its payment and SLA clock were already settled
    // at duly making. Only this one resume transition changes: the other
    // three still land on 'updated' (a submitted-origin item must still be
    // duly made; assessment/decision-origin items must still be reviewed).
    // continue-review-during-duly-made is deliberately RETAINED, now dead for
    // new items but kept as the legacy escape for any item already sitting in
    // 'updated' with a duly-made origin, and as the declaration
    // ReAccreditationUpdatedOrigin.ResolveOriginatingStateId still reads for
    // those. Items snapshotted before v14 keep resume-during-duly-made ->
    // updated until ReAccreditationResumeDulyMadeAssessmentSnapshotMigration
    // patches their frozen snapshot.
    // v15 (RA-595, "add" step): added start-assessment (duly-made ->
    // assessment-in-progress), display name "Assessment started". It is the
    // step management-fe will apply (via the generic action route, so it is
    // CallerInvocable) to move a duly-made case into assessment, replacing
    // payment-received. payment-received is deliberately KEPT at v15 so the
    // currently deployed management-fe keeps working; v16 removes it once the
    // frontend has switched. Decisions and sla-extend stay assessment-only.
    // Items snapshotted before v15 have no start-assessment transition until
    // ReAccreditationStartAssessmentSnapshotMigration patches their frozen
    // snapshot.
    // v16 (RA-595, "remove" step): removed payment-received. management-fe
    // now applies start-assessment, so a stale page or hand-crafted request
    // for payment-received is rejected as an unknown action. Existing
    // "Payment received" audit entries keep their stored display names.
    // Items snapshotted before v16 keep payment-received until
    // ReAccreditationRemovePaymentReceivedSnapshotMigration patches their
    // frozen snapshot.
    // v17 (RA-527, two-step determination, step 1 for approvals): added the
    // non-terminal 'approved-pending-contact' state ("Approved") and the
    // make-determination-approved transition into it from
    // 'awaiting-decision' (CallerInvocable: false — only
    // POST /work-items/re-accreditation/{id}/decision reaches it, because it
    // must mint and stamp the accreditation number). An Approved decision now
    // lands there instead of going straight to 'approved'. Step 2 is the
    // confirm-contact-approved transition (approved-pending-contact ->
    // approved, CallerInvocable: false — only
    // POST /work-items/re-accreditation/{id}/confirm-contact reaches it).
    // A refusal follows the same two steps: make-determination-rejected
    // (awaiting-decision -> the non-terminal 'rejected-pending-contact',
    // "Refused") then confirm-contact-rejected (rejected-pending-contact ->
    // rejected), both CallerInvocable: false. The single-step 'reject'
    // transition (awaiting-decision -> rejected) is removed, so nothing
    // refuses an application without both steps. v17 was still unreleased
    // when the refusal half was added, so it joined v17 rather than a v18.
    // Items snapshotted before v17 have none of these until
    // ReAccreditationPendingContactSnapshotMigration patches their frozen
    // snapshot.
    // v18 (RA-527 follow-up, starting a case keeps it "Duly made"): added the
    // non-terminal 'assessment-started' state ("Duly made") and retargeted
    // start-assessment to it (duly-made -> assessment-started), so starting
    // work no longer shows "Updated" to the regulator or the operator. Its
    // action id, display name and caller-invocability are unchanged. Added
    // submit-for-decision from 'assessment-started' (reusing the action id,
    // still CallerInvocable: false) so Make determination works from a
    // started case. Nothing but start-assessment enters the new state.
    // ReAccreditationStatusPushHook never pushes a move onto it. Added
    // sla-extend from 'assessment-started' to 'assessment-in-progress'
    // (CallerInvocable: false, applied by SlaService.ExtendAsync), so a
    // deadline change on a started case still ends at "Updated" on both sides.
    // Added query-during-assessment and withdraw-during-assessment from
    // 'assessment-started' (reusing the action ids, caller-invocable), so a
    // started case can be queried and withdrawn. Items snapshotted before v18
    // have none of these, and start-assessment still lands on
    // 'assessment-in-progress', until
    // ReAccreditationAssessmentStartedSnapshotMigration patches their frozen
    // snapshot. No data migration: cases already in 'assessment-in-progress'
    // stay there.
    // RA-739: still v18. Moving the states and transitions into the shared
    // StandardWorkflow changed none of them. The version stays on the type:
    // a later change to StandardWorkflow needs a new version and snapshot
    // migration here and on every other type that references it.
    public string TemplateVersion => "v18";

    public string? Category => null;
    public bool UsesSubmissionReference => false;
    public WorkItemWorkflow Workflow => StandardWorkflow.Definition;
    public WorkItemState InitialState => Workflow.InitialState;
    public IReadOnlyCollection<WorkItemState> States => Workflow.States;
    public IReadOnlyCollection<WorkItemTransition> Transitions => Workflow.Transitions;
}