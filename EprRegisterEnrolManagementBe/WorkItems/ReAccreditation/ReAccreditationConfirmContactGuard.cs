using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527: the rules for step 2 of a determination ("confirm applicant
/// contact"), for both outcomes, in one place.
/// <see cref="ReAccreditationConfirmContactService"/> applies them BEFORE the
/// operator-journey push, so the operator is never told "Granted" or
/// "Refused" about an item the write would then refuse, and the write itself
/// (<see cref="ReAccreditationApprovalService.ApproveAsync"/> or
/// <see cref="ReAccreditationRefusalService.RefuseAsync"/>) applies them again
/// on every attempt, against the item as it is then.
/// </summary>
internal static class ReAccreditationConfirmContactGuard
{
    private const string AccreditationIdKey = "accreditationId";

    // RA-448 phase 2: mirrors the backend's own AC1 format regex
    // (accreditation-number-generation.md), anchored to the 'A' prefix since
    // this module only ever deals with accreditation numbers. Explicit
    // matchTimeout: every quantifier here is bounded ({2}/{6}/{4}) with no
    // nested/overlapping repetition, so catastrophic backtracking isn't
    // actually reachable — but a fixed timeout costs nothing and is what
    // static analysis (and defence-in-depth) expects of any Regex applied
    // to a value that ultimately originates from a persisted document
    // rather than a compile-time constant.
    private static readonly Regex s_expectedAccreditationNumberFormat = new(
        @"^A\d{2}(ER|EX|NR|NX|SR|SX|WR|WX)\d{6}\d{4}(AL|FB|GO|GR|PA|PL|ST|WO)$",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250)
    );

    /// <summary>
    /// One outcome's step 2: the pending state it starts from, the terminal
    /// state it lands in and the transition between them.
    /// </summary>
    internal sealed record Step(
        ReAccreditationDecisionOutcome Outcome,
        string PendingStateId,
        string TerminalStateId,
        string ActionId
    );

    public static readonly Step Approval = new(
        ReAccreditationDecisionOutcome.Approved,
        ReAccreditationType.ApprovedPendingContactStateId,
        ReAccreditationType.ApprovedStateId,
        ReAccreditationType.ConfirmContactApprovedActionId
    );

    public static readonly Step Refusal = new(
        ReAccreditationDecisionOutcome.Rejected,
        ReAccreditationType.RejectedPendingContactStateId,
        ReAccreditationType.RejectedStateId,
        ReAccreditationType.ConfirmContactRejectedActionId
    );

    private static readonly Step[] s_steps = [Approval, Refusal];

    /// <summary>The step that starts from <paramref name="stateId"/>, if it is a pending state.</summary>
    public static Step? ForPendingState(string? stateId) =>
        s_steps.FirstOrDefault(s => Is(stateId, s.PendingStateId));

    /// <summary>
    /// Can step 2 apply the determination to <paramref name="workItem"/> now?
    /// Returns null when it can, or the result to hand straight back: a
    /// failure, or an idempotent replay for an item already in the step's
    /// terminal state (a retry, or a second click, after the first
    /// confirmation landed).
    ///
    /// <paramref name="step"/> names the outcome a write is about to apply;
    /// left null (the up-front check, before the push), it is read off the
    /// item's own state, and either terminal decision state is a replay.
    ///
    /// The item must be in the step's pending state and its own frozen
    /// snapshot (or, for a legacy item with none, the registered type) must
    /// declare the confirm-contact transition. An approval must also carry the
    /// real-format accreditation number step 1 stamped: step 2 reuses that
    /// number and never mints one, so an item without it needs a human, not a
    /// new number.
    /// </summary>
    public static WorkItemActionResult? Check(
        Guid workItemId,
        WorkItem? workItem,
        IWorkItemRegistry registry,
        Step? step = null
    )
    {
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (!Is(workItem.TypeId, ReAccreditationType.Id))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
            );
        }

        var replayStates = step is null ? s_steps : [step];
        if (replayStates.Any(s => Is(workItem.StateId, s.TerminalStateId)))
        {
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        // Enumerated rather than read off the template, matching the
        // log-decision service: a closed application must be refused even when
        // its frozen snapshot is too old to carry terminal metadata.
        if (
            s_steps.Any(s => Is(workItem.StateId, s.TerminalStateId))
            || Is(workItem.StateId, ReAccreditationType.WithdrawnStateId)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no actions are allowed."
            );
        }

        step ??= ForPendingState(workItem.StateId);
        if (step is null || !Is(workItem.StateId, step.PendingStateId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Applicant contact can only be confirmed for a work item in "
                    + $"'{step?.PendingStateId ?? string.Join("' or '", s_steps.Select(s => s.PendingStateId))}', "
                    + $"but {workItemId} is in '{workItem.StateId}'."
            );
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        var declared =
            template?.Transitions.Any(t =>
                Is(t.ActionId, step.ActionId) && Is(t.FromStateId, workItem.StateId)
            ) == true;
        if (!declared)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{step.ActionId}' is not available for "
                    + $"work item {workItemId} (template '{workItem.TemplateVersion}')."
            );
        }

        if (step.Outcome != ReAccreditationDecisionOutcome.Approved)
        {
            return null;
        }

        var accreditationId = ReadString(workItem.Payload, AccreditationIdKey);
        if (
            string.IsNullOrWhiteSpace(accreditationId)
            || !s_expectedAccreditationNumberFormat.IsMatch(accreditationId)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item '{workItemId}' is in '{workItem.StateId}' but does not carry the "
                    + $"accreditation id step 1 issued (found '{accreditationId}'). "
                    + "Manual investigation required."
            );
        }

        return null;
    }

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(BsonDocument? document, string fieldName)
    {
        if (document is null || !document.TryGetValue(fieldName, out var value))
        {
            return null;
        }
        return value.IsBsonNull ? null : value.ToString();
    }
}
