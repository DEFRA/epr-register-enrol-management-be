using System.Security.Claims;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Framework service object that drives task completion and state transitions
/// for every work item type. Lives in core because the rules ("you cannot
/// approve a work item with outstanding tasks", "you cannot invoke an action
/// that does not apply to the current state", etc.) are universal across
/// modules. Module-specific business logic belongs in module service objects
/// that may be called before/after this engine.
/// </summary>
public interface IWorkItemService
{
    Task<WorkItemActionResult> CompleteTaskAsync(
        Guid workItemId,
        string taskId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assign (or re-assign) a work item to <paramref name="assigneeId"/>,
    /// snapshotting <paramref name="assigneeName"/> alongside the id so list
    /// views do not need a separate user lookup. Authorization rules:
    /// the caller must either hold the <c>assign</c> role, or be assigning
    /// the item to themselves while it is currently unassigned.
    /// </summary>
    Task<WorkItemActionResult> AssignAsync(
        Guid workItemId,
        string assigneeId,
        string? assigneeName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear the current assignment. Requires the caller to hold the
    /// <c>assign</c> role.
    /// </summary>
    Task<WorkItemActionResult> UnassignAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Append a free-text note (RA-96) to a work item. Authoring identity is
    /// snapshotted from the supplied <see cref="ClaimsPrincipal"/> at the
    /// time of the call so the audit narrative survives later directory
    /// changes. Notes are append-only; this is the only mutation the
    /// framework offers for them.
    /// </summary>
    Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomic compound mutation: append a note AND mark a task complete in a
    /// single document write so a partial failure cannot leave a work item
    /// with an "orphan" note attached to an unfinished task (the bug behind
    /// a workflow that called <see cref="AddNoteAsync"/> followed by
    /// <see cref="CompleteTaskAsync"/> and saw the second call fail with
    /// the first call already persisted). Both halves are validated before
    /// any in-memory mutation happens; on any validation failure the
    /// document is unchanged and no audit entries are written. On success
    /// the framework writes one <c>note-added</c> audit entry plus — only
    /// when the task was not already complete — one <c>task-completed</c>
    /// entry, followed by exactly one
    /// <see cref="IWorkItemPersistence.ReplaceAsync"/>. Re-completing an
    /// already-complete task is treated as a no-op for the completion half
    /// (consistent with <see cref="CompleteTaskAsync"/>); the note is still
    /// appended because note writes are the caller's primary intent.
    /// </summary>
    Task<WorkItemActionResult> AddNoteAndCompleteTaskAsync(
        Guid workItemId,
        string taskId,
        string noteText,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compute the task progress and currently-available actions for a work
    /// item. Returns <c>null</c> when no work item exists with the supplied id.
    /// </summary>
    Task<WorkItemEngineProjection?> ProjectAsync(Guid workItemId, CancellationToken cancellationToken = default);

    /// <summary>Project an already-loaded work item. Pure; safe to call without I/O.</summary>
    WorkItemEngineProjection Project(WorkItem workItem);
}

/// <summary>Snapshot of a work item alongside its engine-derived view.</summary>
public sealed record WorkItemEngineProjection(
    WorkItem WorkItem,
    string TemplateVersion,
    IReadOnlyCollection<WorkItemTaskProgress> Tasks,
    IReadOnlyCollection<WorkItemTransition> AvailableActions);

public sealed class WorkItemService(
    IWorkItemRegistry registry,
    IWorkItemPersistence persistence,
    ILogger<WorkItemService> logger,
    TimeProvider? timeProvider = null) : IWorkItemService
{
    /// <summary>
    /// Role that grants the holder the ability to assign / re-assign /
    /// unassign any work item to anyone. Standard users without this role
    /// can only self-assign unassigned items.
    /// </summary>
    public const string AssignRole = "assign";

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<WorkItemActionResult> CompleteTaskAsync(
        Guid workItemId,
        string taskId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var (workItem, template, failure) = await LoadAsync(workItemId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var tasks = template!.GetTasksForState(workItem!.StateId);
        var task = tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TaskNotApplicable,
                $"Task '{taskId}' is not required for work item {workItemId} in state '{workItem.StateId}'.");
        }

        var bucket = GetCompletedBucket(workItem, workItem.StateId);
        if (!bucket.Add(task.Id))
        {
            // Already completed: idempotent replay. Persist nothing, write
            // no audit entry, but tell the caller this was a no-op so they
            // can render an appropriate UI state instead of a confusing
            // "already done" error.
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.LastModifiedAt = now;
        AppendAudit(workItem, "task-completed", "Task completed", user, now, new()
        {
            ["taskId"] = task.Id,
            ["taskDisplayName"] = task.DisplayName,
            ["stateId"] = workItem.StateId
        });
        try
        {
            await persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        logger.LogInformation(
            "Task {TaskId} marked complete on work item {WorkItemId} ({TypeId}) by {User}",
            task.Id, workItem.Id, workItem.TypeId, DescribeUser(user));

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var (workItem, template, failure) = await LoadAsync(workItemId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var transition = template!.Transitions.FirstOrDefault(
            t => string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        if (transition is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Action '{actionId}' is not declared by work item type '{workItem!.TypeId}'.");
        }

        var currentState = template.States.FirstOrDefault(
            s => string.Equals(s.Id, workItem!.StateId, StringComparison.OrdinalIgnoreCase));
        if (currentState?.IsTerminal == true)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{currentState.Id}'; no actions are allowed.");
        }

        if (!string.Equals(transition.FromStateId, workItem!.StateId, StringComparison.OrdinalIgnoreCase))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{actionId}' moves work items from '{transition.FromStateId}', " +
                $"but {workItemId} is in '{workItem.StateId}'.");
        }

        if (transition.RequiresAllTasksComplete && HasIncompleteTasks(template, workItem))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.IncompleteTasks,
                $"Action '{actionId}' requires every task for state '{workItem.StateId}' to be complete first.");
        }

        if (transition.RequiredRoles is { Count: > 0 } requiredRoles
            && !requiredRoles.Any(r => user?.IsInRole(r) == true))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.NotAuthorized,
                $"Action '{actionId}' requires one of the following roles: {string.Join(", ", requiredRoles)}.");
        }

        var previousState = workItem.StateId;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.StateId = transition.ToStateId;
        workItem.LastModifiedAt = now;
        AppendAudit(workItem, "action-applied", "Action applied", user, now, new()
        {
            ["actionId"] = transition.ActionId,
            ["actionDisplayName"] = transition.DisplayName,
            ["fromStateId"] = previousState,
            ["toStateId"] = workItem.StateId
        });
        try
        {
            await persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) transitioned from {FromState} to {ToState} via action {ActionId} by {User}",
            workItem.Id, workItem.TypeId, previousState, workItem.StateId, transition.ActionId, DescribeUser(user));

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> AssignAsync(
        Guid workItemId,
        string assigneeId,
        string? assigneeName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (string.IsNullOrWhiteSpace(assigneeId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidAssignment,
                "Assignee id is required.");
        }

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }

        var trimmedAssigneeId = assigneeId.Trim();
        var actorUserId = ResolveActorUserId(user);
        var hasAssignRole = user?.IsInRole(AssignRole) == true;

        // Standard users without the assign role can only "claim" an
        // unassigned item, and only for themselves. Anything else (assigning
        // to someone else, taking an item already owned by another user) is
        // reserved for users with the assign role.
        if (!hasAssignRole)
        {
            var isSelfAssign = actorUserId is not null
                && string.Equals(actorUserId, trimmedAssigneeId, StringComparison.Ordinal);
            if (!isSelfAssign)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.NotAuthorized,
                    "Only users with the 'assign' role can assign work items to other users.");
            }
            if (workItem.AssignedToId is not null
                && !string.Equals(workItem.AssignedToId, trimmedAssigneeId, StringComparison.Ordinal))
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.NotAuthorized,
                    "This work item is already assigned to another user; only users with the 'assign' role can re-assign it.");
            }
        }

        var snapshotName = string.IsNullOrWhiteSpace(assigneeName) ? null : assigneeName.Trim();
        var alreadyAssignedToSameUser = string.Equals(workItem.AssignedToId, trimmedAssigneeId, StringComparison.Ordinal)
            && string.Equals(workItem.AssignedToName, snapshotName, StringComparison.Ordinal);

        if (!alreadyAssignedToSameUser)
        {
            var previousAssigneeId = workItem.AssignedToId;
            var previousAssigneeName = workItem.AssignedToName;
            workItem.AssignedToId = trimmedAssigneeId;
            workItem.AssignedToName = snapshotName;
            workItem.AssignedAt = _timeProvider.GetUtcNow().UtcDateTime;
            workItem.AssignedBy = actorUserId!;
            workItem.LastModifiedAt = workItem.AssignedAt.Value;
            AppendAudit(workItem, "assigned", "Assigned", user, workItem.AssignedAt.Value, new()
            {
                ["assigneeId"] = trimmedAssigneeId,
                ["assigneeName"] = snapshotName,
                ["previousAssigneeId"] = previousAssigneeId,
                ["previousAssigneeName"] = previousAssigneeName
            });
            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
            }
            catch (WorkItemConcurrencyException)
            {
                return ConcurrencyConflict(workItem.Id);
            }
            logger.LogInformation(
                "Work item {WorkItemId} ({TypeId}) assigned from {PreviousAssignee} to {NewAssignee} by {User}",
                workItem.Id, workItem.TypeId, previousAssigneeId ?? "(unassigned)", trimmedAssigneeId, DescribeUser(user));
        }

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> UnassignAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }

        if (user?.IsInRole(AssignRole) != true)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.NotAuthorized,
                "Only users with the 'assign' role can unassign work items.");
        }

        if (workItem.AssignedToId is null)
        {
            // Idempotent: already unassigned, nothing to do.
            return WorkItemActionResult.Success(workItem);
        }

        var previousAssigneeId = workItem.AssignedToId;
        var previousAssigneeName = workItem.AssignedToName;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.AssignedToId = null;
        workItem.AssignedToName = null;
        workItem.AssignedAt = null;
        workItem.AssignedBy = null;
        workItem.LastModifiedAt = now;
        AppendAudit(workItem, "unassigned", "Unassigned", user, now, new()
        {
            ["previousAssigneeId"] = previousAssigneeId,
            ["previousAssigneeName"] = previousAssigneeName
        });
        try
        {
            await persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) unassigned (was {PreviousAssignee}) by {User}",
            workItem.Id, workItem.TypeId, previousAssigneeId, DescribeUser(user));

        return WorkItemActionResult.Success(workItem);
    }

    /// <summary>
    /// Maximum length of a single note body. Picked to comfortably hold a
    /// long paragraph of assessor narrative without enabling the field as a
    /// dumping ground for documents. Enforced at the service boundary so
    /// callers see the same limit regardless of transport.
    /// </summary>
    public const int MaxNoteLength = 4000;

    public async Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                "Note text is required.");
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxNoteLength)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                $"Note text must be {MaxNoteLength} characters or fewer.");
        }

        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }

        var note = new WorkItemNote
        {
            Text = trimmed,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = ResolveActorUserId(user)!,
            CreatedByName = user?.FindFirstValue("user:name")
        };
        workItem.Notes.Add(note);
        workItem.LastModifiedAt = note.CreatedAt;
        AppendAudit(workItem, "note-added", "Note added", user, note.CreatedAt, new()
        {
            ["noteId"] = note.Id.ToString()
        });
        try
        {
            await persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        logger.LogInformation(
            "Note {NoteId} added to work item {WorkItemId} ({TypeId}) by {User}",
            note.Id, workItem.Id, workItem.TypeId, DescribeUser(user));

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> AddNoteAndCompleteTaskAsync(
        Guid workItemId,
        string taskId,
        string noteText,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        // Validate the note up-front so the document is untouched on a bad
        // request (parity with AddNoteAsync's contract).
        if (string.IsNullOrWhiteSpace(noteText))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                "Note text is required.");
        }
        var trimmed = noteText.Trim();
        if (trimmed.Length > MaxNoteLength)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                $"Note text must be {MaxNoteLength} characters or fewer.");
        }

        var (workItem, template, failure) = await LoadAsync(workItemId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var tasks = template!.GetTasksForState(workItem!.StateId);
        var task = tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
        if (task is null)
        {
            // Validation failure before any mutation: the document is
            // unchanged, no note appended, no audit entries written.
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TaskNotApplicable,
                $"Task '{taskId}' is not required for work item {workItemId} in state '{workItem.StateId}'.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Mutate in memory only — nothing is persisted until the single
        // ReplaceAsync below, so an exception or a concurrency failure
        // leaves the on-disk document untouched. This is the whole point
        // of the compound method.
        var note = new WorkItemNote
        {
            Text = trimmed,
            CreatedAt = now,
            CreatedBy = ResolveActorUserId(user)!,
            CreatedByName = user?.FindFirstValue("user:name")
        };
        workItem.Notes.Add(note);
        AppendAudit(workItem, "note-added", "Note added", user, now, new()
        {
            ["noteId"] = note.Id.ToString()
        });

        // Re-completing an already-complete task is a no-op for the
        // completion half (matches CompleteTaskAsync's idempotency
        // contract: no audit entry for the no-op). The note is still
        // written — note writes are the caller's primary intent here.
        var bucket = GetCompletedBucket(workItem, workItem.StateId);
        var taskNewlyCompleted = bucket.Add(task.Id);
        if (taskNewlyCompleted)
        {
            AppendAudit(workItem, "task-completed", "Task completed", user, now, new()
            {
                ["taskId"] = task.Id,
                ["taskDisplayName"] = task.DisplayName,
                ["stateId"] = workItem.StateId
            });
        }

        workItem.LastModifiedAt = now;

        try
        {
            await persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }

        logger.LogInformation(
            "Note {NoteId} added and task {TaskId} {CompletionOutcome} on work item {WorkItemId} ({TypeId}) by {User}",
            note.Id,
            task.Id,
            taskNewlyCompleted ? "marked complete" : "left as already-complete",
            workItem.Id,
            workItem.TypeId,
            DescribeUser(user));

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemEngineProjection?> ProjectAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        return workItem is null ? null : Project(workItem);
    }

    public WorkItemEngineProjection Project(WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            // The work item exists but its module is no longer registered and
            // no snapshot is on file (e.g. a legacy item). Render it as having
            // no tasks and no available actions so callers can still display it.
            return new WorkItemEngineProjection(
                workItem,
                ResolveTemplateVersion(workItem),
                Array.Empty<WorkItemTaskProgress>(),
                Array.Empty<WorkItemTransition>());
        }

        var completed = workItem.CompletedTaskIdsByState.TryGetValue(workItem.StateId, out var done)
            ? done
            : new HashSet<string>();

        var taskProgress = template.GetTasksForState(workItem.StateId)
            .Select(task => new WorkItemTaskProgress(task.Id, task.DisplayName, completed.Contains(task.Id)))
            .ToList();

        var currentState = template.States.FirstOrDefault(
            s => string.Equals(s.Id, workItem.StateId, StringComparison.OrdinalIgnoreCase));
        var isTerminal = currentState?.IsTerminal == true;

        IReadOnlyCollection<WorkItemTransition> available = isTerminal
            ? Array.Empty<WorkItemTransition>()
            : template.Transitions
                .Where(t => string.Equals(t.FromStateId, workItem.StateId, StringComparison.OrdinalIgnoreCase))
                .Where(t => !t.RequiresAllTasksComplete || taskProgress.All(p => p.IsComplete))
                .ToList();

        return new WorkItemEngineProjection(workItem, template.TemplateVersion, taskProgress, available);
    }

    private async Task<(WorkItem? WorkItem, IWorkItemTemplate? Template, WorkItemActionResult? Failure)> LoadAsync(
        Guid workItemId, CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return (null, null, WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."));
        }

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            return (workItem, null, WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} references unregistered type '{workItem.TypeId}' and has no stored template snapshot."));
        }

        return (workItem, template, null);
    }

    /// <summary>
    /// Pick the template the engine should reason about for a work item. The
    /// snapshot stored on the work item wins so that historical items keep
    /// their original task list and action set even if the live type has
    /// since changed; the live type is used only as a fallback for legacy
    /// items submitted before snapshots existed.
    /// </summary>
    private IWorkItemTemplate? ResolveTemplate(WorkItem workItem)
    {
        if (workItem.TemplateSnapshot is not null)
        {
            return workItem.TemplateSnapshot;
        }
        return registry.Find(workItem.TypeId);
    }

    private string ResolveTemplateVersion(WorkItem workItem) =>
        workItem.TemplateVersion
        ?? workItem.TemplateSnapshot?.TemplateVersion
        ?? registry.Find(workItem.TypeId)?.TemplateVersion
        ?? "unknown";

    private static HashSet<string> GetCompletedBucket(WorkItem workItem, string stateId)
    {
        if (!workItem.CompletedTaskIdsByState.TryGetValue(stateId, out var bucket))
        {
            bucket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            workItem.CompletedTaskIdsByState[stateId] = bucket;
        }
        return bucket;
    }

    private static bool HasIncompleteTasks(IWorkItemTemplate template, WorkItem workItem)
    {
        var required = template.GetTasksForState(workItem.StateId);
        if (required.Count == 0)
        {
            return false;
        }
        var completed = workItem.CompletedTaskIdsByState.TryGetValue(workItem.StateId, out var done)
            ? done
            : (IReadOnlyCollection<string>)Array.Empty<string>();
        return required.Any(t => !completed.Contains(t.Id));
    }

    /// <summary>
    /// Append a single entry to the work item's audit log (RA-97). Called
    /// from every engine method on the success path so modules inherit a
    /// complete, automatic audit trail without writing any audit code
    /// themselves. Identity is snapshotted from the supplied principal at
    /// write time.
    /// </summary>
    private static void AppendAudit(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal? user,
        DateTime createdAt,
        Dictionary<string, string?> details)
    {
        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = action,
            ActionDisplayName = actionDisplayName,
            Details = details,
            CreatedAt = createdAt,
            CreatedBy = ResolveActorUserId(user)!,
            CreatedByName = user?.FindFirstValue("user:name")
        });
    }

    private static WorkItemActionResult ConcurrencyConflict(Guid workItemId) =>
        WorkItemActionResult.Failure(
            WorkItemActionFailureCode.ConcurrencyConflict,
            $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry.");

    private static string DescribeUser(ClaimsPrincipal? user) =>
        user?.FindFirstValue("user:id")
        ?? user?.FindFirstValue("cognito:client_id")
        ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";

    /// <summary>
    /// Mutating operations require an end-user identity (forwarded by the
    /// BFF as the <c>user:id</c> claim). When it is missing the engine
    /// refuses the call rather than recording an audit entry that cannot be
    /// traced back to a real human — client_id and "unknown" are NOT
    /// acceptable substitutes for accountability.
    /// </summary>
    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal? user)
    {
        if (ResolveActorUserId(user) is not null)
        {
            return null;
        }
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.MissingActorIdentity,
            "Mutating this work item requires an authenticated end user; " +
            "the request did not include a 'user:id' claim.");
    }

    /// <summary>
    /// The acting end-user's identifier as forwarded by the BFF. Falls back
    /// to <c>null</c> when the request only carries a service identity (e.g.
    /// machine-to-machine calls), which is enough for the service to treat
    /// the call as "no human acting" for assignment purposes.
    /// </summary>
    private static string? ResolveActorUserId(ClaimsPrincipal? user)
    {
        var id = user?.FindFirstValue("user:id");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}