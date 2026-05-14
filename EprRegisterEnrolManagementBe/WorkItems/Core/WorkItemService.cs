using System.Security.Claims;
using MongoDB.Bson;

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
{/// <summary>
    /// Create a brand-new work item of <paramref name="type"/> and persist
    /// it. Captures a frozen template snapshot, stamps a server-side
    /// submission timestamp from the injected <see cref="TimeProvider"/>,
    /// and appends a single <c>work-item-submitted</c> entry to the new
    /// work item's audit log so the audit timeline starts at the
    /// document's birth event rather than the first task completion. The
    /// audit entry and the document body are written in the same
    /// <see cref="IWorkItemPersistence.CreateAsync"/> call. Mutations
    /// require a <c>user:id</c> claim — calls without one return
    /// <see cref="WorkItemActionFailureCode.MissingActorIdentity"/> and
    /// nothing is persisted.
    /// </summary>
    Task<WorkItemActionResult> SubmitAsync(
        IWorkItemType type,
        BsonDocument payload,
        string? submittedBy,
        ClaimsPrincipal user,
        IReadOnlyDictionary<string, string?>? submissionMetadata = null,
        CancellationToken cancellationToken = default);

    
    Task<WorkItemActionResult> CompleteTaskAsync(
        Guid workItemId,
        string taskId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Set the lifecycle <see cref="WorkItemTaskStatus"/> of a single task
    /// (epr-gl6). Generalises <see cref="CompleteTaskAsync"/> to the full
    /// status set (NotStarted / InProgress / Blocked / Completed) while
    /// keeping <see cref="WorkItem.CompletedTaskIdsByState"/> in sync so
    /// older readers continue to work.
    ///
    /// Idempotent: if the task is already in the requested status the call
    /// returns success without writing an audit entry, matching the
    /// framework rule that no-ops do not record audit. On a real change
    /// the engine appends a single <c>task-status-changed</c> entry whose
    /// <c>Details</c> carry the task id and the from/to status names — no
    /// separate <c>task-completed</c> entry is emitted when transitioning
    /// to <see cref="WorkItemTaskStatus.Completed"/> via this call (the
    /// status change is the canonical record). The legacy
    /// <see cref="CompleteTaskAsync"/> path keeps emitting
    /// <c>task-completed</c> for back-compat with existing audit consumers.
    /// </summary>
    Task<WorkItemActionResult> SetTaskStatusAsync(
        Guid workItemId,
        string taskId,
        WorkItemTaskStatus status,
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
    ///
    /// When <paramref name="taskId"/> is <c>null</c> (the default) the note
    /// is a work-item-level note: persisted with <c>TaskId = null</c> and
    /// audited as <c>note-added</c> (RA-96 behaviour).
    ///
    /// When <paramref name="taskId"/> is set (RA-129 / epr-cky) the note is
    /// scoped to a task on the work item's current state. The id is
    /// validated against the resolved template's task list — an unknown
    /// id returns
    /// <see cref="WorkItemActionFailureCode.TaskNotApplicable"/> with no
    /// mutation. On success the persisted <c>WorkItemNote.TaskId</c> is
    /// populated and the audit entry becomes <c>task-note-added</c> with
    /// details <c>{ taskId, taskDisplayName, noteId, excerpt }</c> where
    /// <c>excerpt</c> is the first
    /// <see cref="TaskNoteAuditExcerptLength"/> characters of the trimmed
    /// note body. Note write + audit entry are a single atomic
    /// <see cref="IWorkItemPersistence.ReplaceAsync"/>.
    /// </summary>
    Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default,
        string? taskId = null);

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

public sealed class WorkItemService : IWorkItemService
{
    /// <summary>
    /// Role that grants the holder the ability to assign / re-assign /
    /// unassign any work item to anyone. Standard users without this role
    /// can only self-assign unassigned items.
    /// </summary>
    public const string AssignRole = "assign";

    private readonly IWorkItemRegistry _registry;
    private readonly IWorkItemPersistence _persistence;
    private readonly ILogger<WorkItemService> _logger;
    private readonly IReadOnlyCollection<IWorkItemPostActionHook> _postActionHooks;
    private readonly TimeProvider _timeProvider;

    public WorkItemService(
        IWorkItemRegistry registry,
        IWorkItemPersistence persistence,
        ILogger<WorkItemService> logger,
        TimeProvider? timeProvider = null,
        IEnumerable<IWorkItemPostActionHook>? postActionHooks = null)
    {
        this._registry = registry;
        this._persistence = persistence;
        this._logger = logger;
        _postActionHooks = postActionHooks?.ToArray() ?? Array.Empty<IWorkItemPostActionHook>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkItemActionResult> SubmitAsync(
        IWorkItemType type,
        BsonDocument payload,
        string? submittedBy,
        ClaimsPrincipal user,
        IReadOnlyDictionary<string, string?>? submissionMetadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payload);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var snapshot = WorkItemTemplateSnapshot.Capture(type);
        var workItem = new WorkItem
        {
            TypeId = type.TypeId,
            StateId = type.InitialState.Id,
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = submittedBy,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            Payload = payload
        };

        // Birth event: the audit timeline must start at submission rather
        // than the first task completion. Appended to the in-memory list
        // before the single CreateAsync call so the document and its first
        // audit entry land in storage together.
        //
        // RA-126: enrich the birth entry with the originating BFF /
        // application context so audit consumers can reconstruct who
        // submitted from where without joining back to request logs.
        // 'source' / 'applicationReference' are caller-supplied (BFF /
        // operator FE forward whichever apply); 'clientId' is the
        // Cognito client id forwarded by the CDP gateway; 'userId' is
        // the end-user identity claim required for any mutation.
        AppendAudit(workItem, "work-item-submitted", "Work item submitted", user, now, new()
        {
            ["typeId"] = type.TypeId,
            ["stateId"] = workItem.StateId,
            ["templateVersion"] = snapshot.TemplateVersion,
            ["source"] = submissionMetadata is not null
                && submissionMetadata.TryGetValue("source", out var src) ? src : null,
            ["clientId"] = user.FindFirstValue("cognito:client_id"),
            ["userId"] = ResolveActorUserId(user),
            ["applicationReference"] = submissionMetadata is not null
                && submissionMetadata.TryGetValue("applicationReference", out var appRef) ? appRef : null
        });

        await _persistence.CreateAsync(workItem, cancellationToken);

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) submitted in state {StateId} by {User}",
            workItem.Id, workItem.TypeId, workItem.StateId, DescribeUser(user));

        await InvokeSubmittedHooksAsync(workItem, user, cancellationToken);

        return WorkItemActionResult.Success(workItem);
    }

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

        // epr-gl6: dual-write — the per-task status map is the canonical
        // source of truth for the new status set, but
        // CompletedTaskIdsByState is retained for one release cycle so
        // legacy readers continue to work. Keep them in lockstep.
        SetTaskStatus(workItem, workItem.StateId, task.Id, WorkItemTaskStatus.Completed);

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
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
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
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) transitioned from {FromState} to {ToState} via action {ActionId} by {User}",
            workItem.Id, workItem.TypeId, previousState, workItem.StateId, transition.ActionId, DescribeUser(user));

        await InvokeActionAppliedHooksAsync(workItem, transition.ActionId, previousState, user, cancellationToken);

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

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
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

        if (alreadyAssignedToSameUser)
        {
            // Re-assigning to the same user is a no-op: persist nothing,
            // write no audit entry, but tell the caller this was a replay
            // (parity with CompleteTaskAsync) so the endpoint can surface
            // X-Idempotent-Replay: true and the UI can render an
            // appropriate state instead of a misleading "newly assigned".
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

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
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) assigned from {PreviousAssignee} to {NewAssignee} by {User}",
            workItem.Id, workItem.TypeId, previousAssigneeId ?? "(unassigned)", trimmedAssigneeId, DescribeUser(user));

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

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
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
            // Idempotent: already unassigned, nothing to do. Flag as a
            // replay (parity with CompleteTaskAsync / AssignAsync) so the
            // endpoint can set X-Idempotent-Replay: true.
            return WorkItemActionResult.IdempotentReplay(workItem);
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
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
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

    /// <summary>
    /// Length of the snapshot copied into a <c>task-note-added</c> audit
    /// entry's <c>excerpt</c> detail (RA-129 / epr-cky). Audit entries are
    /// scanned in list views; the full note body lives on
    /// <c>WorkItem.Notes</c> for the rare case a reader needs the rest.
    /// </summary>
    public const int TaskNoteAuditExcerptLength = 100;

    public async Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default,
        string? taskId = null)
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

        // Two paths intentionally diverge here:
        //  * work-item-level note (taskId null): historical RA-96 path —
        //    a single GetByIdAsync, no template resolution required, so
        //    the note still works for legacy items whose module is no
        //    longer registered and which carry no template snapshot.
        //  * task-level note (RA-129): the task id must be validated
        //    against the resolved template's tasks for the current
        //    state, mirroring CompleteTaskAsync's TaskNotApplicable
        //    contract for unknown ids. We therefore go through LoadAsync
        //    so a missing template surfaces as a structured failure
        //    instead of a NullReferenceException.
        WorkItem workItem;
        WorkItemTask? task = null;
        if (taskId is null)
        {
            var loaded = await _persistence.GetByIdAsync(workItemId, cancellationToken);
            if (loaded is null)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.WorkItemNotFound,
                    $"No work item exists with id '{workItemId}'.");
            }
            workItem = loaded;
        }
        else
        {
            var (loaded, template, failure) = await LoadAsync(workItemId, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            workItem = loaded!;
            var tasks = template!.GetTasksForState(workItem.StateId);
            task = tasks.FirstOrDefault(t => string.Equals(t.Id, taskId, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                // Validation failure before any mutation: document is
                // unchanged, no note appended, no audit entry written.
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.TaskNotApplicable,
                    $"Task '{taskId}' is not required for work item {workItemId} in state '{workItem.StateId}'.");
            }
        }

        var note = new WorkItemNote
        {
            Text = trimmed,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = ResolveActorUserId(user)!,
            CreatedByName = user?.FindFirstValue("user:name"),
            // Use the resolved task's canonical id rather than echoing
            // the caller's casing so on-disk values stay in lockstep
            // with the template even when the BFF forwards a casing
            // variant.
            TaskId = task?.Id
        };
        workItem.Notes.Add(note);
        workItem.LastModifiedAt = note.CreatedAt;

        if (task is null)
        {
            AppendAudit(workItem, "note-added", "Note added", user, note.CreatedAt, new()
            {
                ["noteId"] = note.Id.ToString(),
                // Snapshot the trimmed body so the audit log is self-describing —
                // a reader does not need to cross-reference Notes by id to see
                // what was written. Already capped by MaxNoteLength.
                ["noteText"] = note.Text
            });
        }
        else
        {
            // RA-129 spec (user-stories.md): excerpt is the first 100
            // characters of the trimmed body. Audit consumers scan
            // entries in list views; the full body is still available
            // via WorkItem.Notes for the rare case a reader wants the
            // rest.
            var excerpt = trimmed.Length <= TaskNoteAuditExcerptLength
                ? trimmed
                : trimmed[..TaskNoteAuditExcerptLength];
            AppendAudit(workItem, "task-note-added", "Task note added", user, note.CreatedAt, new()
            {
                ["taskId"] = task.Id,
                ["taskDisplayName"] = task.DisplayName,
                ["noteId"] = note.Id.ToString(),
                ["excerpt"] = excerpt
            });
        }

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Note {NoteId} added to work item {WorkItemId} ({TypeId}) {TaskScope} by {User}",
            note.Id, workItem.Id, workItem.TypeId,
            task is null ? "(work-item-level)" : $"for task {task.Id}",
            DescribeUser(user));

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
            ["noteId"] = note.Id.ToString(),
            // Snapshot the trimmed body so the audit log is self-describing —
            // a reader does not need to cross-reference Notes by id to see
            // what was written. Already capped by MaxNoteLength.
            ["noteText"] = note.Text
        });

        // Re-completing an already-complete task is a no-op for the
        // completion half (matches CompleteTaskAsync's idempotency
        // contract: no audit entry for the no-op). The note is still
        // written — note writes are the caller's primary intent here.
        var bucket = GetCompletedBucket(workItem, workItem.StateId);
        var taskNewlyCompleted = bucket.Add(task.Id);
        if (taskNewlyCompleted)
        {
            // epr-gl6: dual-write the per-task status map alongside the
            // legacy CompletedTaskIdsByState bucket so both sources of
            // truth stay in lockstep.
            SetTaskStatus(workItem, workItem.StateId, task.Id, WorkItemTaskStatus.Completed);

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
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }

        _logger.LogInformation(
            "Note {NoteId} added and task {TaskId} {CompletionOutcome} on work item {WorkItemId} ({TypeId}) by {User}",
            note.Id,
            task.Id,
            taskNewlyCompleted ? "marked complete" : "left as already-complete",
            workItem.Id,
            workItem.TypeId,
            DescribeUser(user));

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> SetTaskStatusAsync(
        Guid workItemId,
        string taskId,
        WorkItemTaskStatus status,
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

        var currentStatus = GetCurrentTaskStatus(workItem, workItem.StateId, task.Id);
        if (currentStatus == status)
        {
            // Idempotent no-op: framework rule is that no-ops do not write
            // an audit entry. Mirror CompleteTaskAsync's behaviour but use
            // a plain Success rather than IdempotentReplay — the new API
            // does not need to surface replay through a header.
            return WorkItemActionResult.Success(workItem);
        }

        // epr-gl6: dual-write — keep CompletedTaskIdsByState in lockstep
        // with TaskStatusesByState so legacy readers (which only consume
        // the bucket) keep observing a consistent view.
        SetTaskStatus(workItem, workItem.StateId, task.Id, status);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.LastModifiedAt = now;
        AppendAudit(workItem, "task-status-changed", "Task status changed", user, now, new()
        {
            ["taskId"] = task.Id,
            ["taskDisplayName"] = task.DisplayName,
            ["stateId"] = workItem.StateId,
            ["fromStatus"] = currentStatus.ToString(),
            ["toStatus"] = status.ToString()
        });

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }

        _logger.LogInformation(
            "Task {TaskId} on work item {WorkItemId} ({TypeId}) moved from {FromStatus} to {ToStatus} by {User}",
            task.Id, workItem.Id, workItem.TypeId, currentStatus, status, DescribeUser(user));

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemEngineProjection?> ProjectAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
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

        // epr-gl6: per-task status map is the canonical source of truth
        // when present; fall back to the legacy CompletedTaskIdsByState
        // bucket for documents written before the map existed (Completed
        // when in the bucket, NotStarted otherwise).
        var statuses = workItem.TaskStatusesByState.TryGetValue(workItem.StateId, out var stateStatuses)
            ? stateStatuses
            : null;

        var taskProgress = template.GetTasksForState(workItem.StateId)
            .Select(task =>
            {
                var status = statuses is not null && statuses.TryGetValue(task.Id, out var explicitStatus)
                    ? explicitStatus
                    : (completed.Contains(task.Id) ? WorkItemTaskStatus.Completed : WorkItemTaskStatus.NotStarted);
                return new WorkItemTaskProgress(
                    task.Id,
                    task.DisplayName,
                    IsComplete: status == WorkItemTaskStatus.Completed,
                    Status: status);
            })
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
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
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
        return _registry.Find(workItem.TypeId);
    }

    private string ResolveTemplateVersion(WorkItem workItem) =>
        workItem.TemplateVersion
        ?? workItem.TemplateSnapshot?.TemplateVersion
        ?? _registry.Find(workItem.TypeId)?.TemplateVersion
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

    /// <summary>
    /// Resolve the current <see cref="WorkItemTaskStatus"/> of a single task
    /// (epr-gl6). Prefers the per-task status map; falls back to the legacy
    /// <see cref="WorkItem.CompletedTaskIdsByState"/> bucket for documents
    /// written before the map existed.
    /// </summary>
    private static WorkItemTaskStatus GetCurrentTaskStatus(WorkItem workItem, string stateId, string taskId)
    {
        if (workItem.TaskStatusesByState.TryGetValue(stateId, out var inner)
            && inner.TryGetValue(taskId, out var explicitStatus))
        {
            return explicitStatus;
        }
        if (workItem.CompletedTaskIdsByState.TryGetValue(stateId, out var bucket)
            && bucket.Contains(taskId))
        {
            return WorkItemTaskStatus.Completed;
        }
        return WorkItemTaskStatus.NotStarted;
    }

    /// <summary>
    /// Apply a status change to both <see cref="WorkItem.TaskStatusesByState"/>
    /// (canonical) and <see cref="WorkItem.CompletedTaskIdsByState"/>
    /// (legacy duplicate) so the two stay in lockstep on every write — see
    /// epr-gl6. <see cref="WorkItemTaskStatus.Completed"/> adds the task to
    /// the legacy bucket; any other status removes it.
    /// </summary>
    private static void SetTaskStatus(WorkItem workItem, string stateId, string taskId, WorkItemTaskStatus status)
    {
        if (!workItem.TaskStatusesByState.TryGetValue(stateId, out var inner))
        {
            inner = new Dictionary<string, WorkItemTaskStatus>(StringComparer.OrdinalIgnoreCase);
            workItem.TaskStatusesByState[stateId] = inner;
        }
        inner[taskId] = status;

        var bucket = GetCompletedBucket(workItem, stateId);
        if (status == WorkItemTaskStatus.Completed)
        {
            bucket.Add(taskId);
        }
        else
        {
            bucket.Remove(taskId);
        }
    }

    private static bool HasIncompleteTasks(IWorkItemTemplate template, WorkItem workItem)
    {
        var required = template.GetTasksForState(workItem.StateId);
        if (required.Count == 0)
        {
            return false;
        }
        // epr-08y: TaskStatusesByState is the canonical source of truth
        // (epr-gl6 / WorkItem.cs:99-110). Consult it first and only fall
        // back to the legacy CompletedTaskIdsByState bucket when no
        // per-task status is recorded for a task. Reading only the legacy
        // bucket would let a v2 module that writes only to the canonical
        // map silently transition past incomplete tasks.
        return required.Any(t =>
            GetCurrentTaskStatus(workItem, workItem.StateId, t.Id) != WorkItemTaskStatus.Completed);
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

    /// <summary>
    /// Fan out to every registered <see cref="IWorkItemPostActionHook"/>
    /// after a successful submission. Hooks are required to swallow
    /// their own failures (see interface contract); this method only
    /// guards against a misbehaving hook by catching and logging so a
    /// single hook cannot unwind the originating mutation.
    /// </summary>
    private async Task InvokeSubmittedHooksAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnSubmittedAsync(workItem, user, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Post-action submit hook {HookType} failed for work item {WorkItemId}",
                    hook.GetType().FullName, workItem.Id);
            }
        }
    }

    private async Task InvokeActionAppliedHooksAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(workItem, actionId, fromStateId, user, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Post-action transition hook {HookType} failed for work item {WorkItemId} action {ActionId}",
                    hook.GetType().FullName, workItem.Id, actionId);
            }
        }
    }
}