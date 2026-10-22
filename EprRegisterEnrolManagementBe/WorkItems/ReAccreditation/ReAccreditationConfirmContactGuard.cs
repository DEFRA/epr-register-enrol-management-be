using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527: the rules for step 2 of an approval ("confirm applicant contact"),
/// in one place. <see cref="ReAccreditationConfirmContactService"/> applies
/// them BEFORE the operator-journey push, so the operator is never told
/// "Granted" about an item the write would then refuse, and
/// <see cref="ReAccreditationApprovalService.ApproveAsync"/> applies them
/// again on every write attempt, against the item as it is then.
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
    /// Can step 2 apply the approval to <paramref name="workItem"/> now?
    /// Returns null when it can, or the result to hand straight back: a
    /// failure, or an idempotent replay for an item already Granted (a
    /// retry, or a second click, after the first confirmation landed).
    ///
    /// The item must be in <c>approved-pending-contact</c>, its own frozen
    /// snapshot (or, for a legacy item with none, the registered type) must
    /// declare the confirm-contact transition, and it must carry the
    /// real-format accreditation number step 1 stamped. Step 2 reuses that
    /// number and never mints one, so an item without it needs a human, not
    /// a new number.
    /// </summary>
    public static WorkItemActionResult? Check(
        Guid workItemId,
        WorkItem? workItem,
        IWorkItemRegistry registry
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

        if (Is(workItem.StateId, ReAccreditationType.ApprovedStateId))
        {
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        // Enumerated rather than read off the template, matching the
        // log-decision service: a closed application must be refused even when
        // its frozen snapshot is too old to carry terminal metadata.
        if (Is(workItem.StateId, "rejected") || Is(workItem.StateId, "withdrawn"))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no actions are allowed."
            );
        }

        if (!Is(workItem.StateId, ReAccreditationType.ApprovedPendingContactStateId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Applicant contact can only be confirmed for a work item in "
                    + $"'{ReAccreditationType.ApprovedPendingContactStateId}', but {workItemId} is in "
                    + $"'{workItem.StateId}'."
            );
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        var declared =
            template?.Transitions.Any(t =>
                Is(t.ActionId, ReAccreditationType.ConfirmContactApprovedActionId)
                && Is(t.FromStateId, workItem.StateId)
            ) == true;
        if (!declared)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{ReAccreditationType.ConfirmContactApprovedActionId}' is not available for "
                    + $"work item {workItemId} (template '{workItem.TemplateVersion}')."
            );
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
