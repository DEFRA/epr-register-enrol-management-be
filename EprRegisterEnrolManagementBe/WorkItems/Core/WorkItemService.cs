using System.Security.Claims;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// Framework service object that drives state transitions for every work
/// item type. Lives in core because the rules ("you cannot act on a closed
/// case", "you cannot invoke an action
/// that does not apply to the current state", etc.) are universal across
/// modules. Module-specific business logic belongs in module service objects
/// that may be called before/after this engine.
/// </summary>
public interface IWorkItemService
{
    /// <summary>
    /// Create a brand-new work item of <paramref name="type"/> and persist
    /// it. Captures a frozen template snapshot, stamps a server-side
    /// submission timestamp from the injected <see cref="TimeProvider"/>,
    /// and appends a single <c>work-item-submitted</c> entry to the new
    /// work item's audit log so the audit timeline starts at the
    /// document's birth event rather than the first state change. The
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
        CancellationToken cancellationToken = default
    );

    Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Assign (or re-assign) a work item to <paramref name="assigneeId"/>,
    /// snapshotting <paramref name="assigneeName"/> alongside the id so list
    /// views do not need a separate user lookup. RA-323: every caseworker
    /// may assign to anyone, including re-assigning an already-assigned
    /// item — there is no separate permission tier.
    /// </summary>
    Task<WorkItemActionResult> AssignAsync(
        Guid workItemId,
        string assigneeId,
        string? assigneeName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Clear the current assignment. Any authenticated caseworker may
    /// unassign (RA-323).
    /// </summary>
    Task<WorkItemActionResult> UnassignAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Append a free-text work-item-level note (RA-96). Authoring identity is
    /// snapshotted from the supplied <see cref="ClaimsPrincipal"/> at the
    /// time of the call so the audit narrative survives later directory
    /// changes. Notes are append-only; this is the only mutation the
    /// framework offers for them. Audited as <c>note-added</c>. The
    /// withdraw and decision-rationale flows are the only current callers —
    /// the standalone FE "add a note" feature and task-scoped notes have
    /// been removed.
    /// </summary>
    Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Compute the currently-available actions for a work item. Returns
    /// <c>null</c> when no work item exists with the supplied id.
    /// </summary>
    Task<WorkItemEngineProjection?> ProjectAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Project an already-loaded work item. Pure; safe to call without I/O.</summary>
    WorkItemEngineProjection Project(WorkItem workItem);
}

/// <summary>Snapshot of a work item alongside its engine-derived view.</summary>
public sealed record WorkItemEngineProjection(
    WorkItem WorkItem,
    string TemplateVersion,
    IReadOnlyCollection<WorkItemTransition> AvailableActions,
    // RA-410: the state this item returns to when its current waypoint
    // discharges (see IWorkItemOriginStateResolver). Almost always
    // WorkItem.StateId. Defaults to null so callers constructing a projection
    // directly are unaffected; Project always populates it.
    string? OriginStateId = null
);

public sealed class WorkItemService : IWorkItemService
{
    /// <summary>
    /// Maximum number of <c>applicationReference</c> candidates the engine
    /// tries before giving up on a submission. The suffix keyspace is 10^9,
    /// so even with a large backlog a collision is rare; five attempts make
    /// the probability of a spurious failure negligible while bounding the
    /// work done per submission (RA-219).
    /// </summary>
    public const int MaxApplicationReferenceAttempts = 5;

    private readonly IWorkItemRegistry _registry;
    private readonly IWorkItemPersistence _persistence;
    private readonly ILogger<WorkItemService> _logger;
    private readonly IReadOnlyCollection<IWorkItemPostActionHook> _postActionHooks;
    private readonly TimeProvider _timeProvider;
    private readonly IApplicationReferenceGenerator _referenceGenerator;
    private readonly IReadOnlyCollection<IWorkItemOriginStateResolver> _originStateResolvers;

    public WorkItemService(
        IWorkItemRegistry registry,
        IWorkItemPersistence persistence,
        ILogger<WorkItemService> logger,
        TimeProvider? timeProvider = null,
        IEnumerable<IWorkItemPostActionHook>? postActionHooks = null,
        IApplicationReferenceGenerator? referenceGenerator = null,
        IEnumerable<IWorkItemOriginStateResolver>? originStateResolvers = null
    )
    {
        this._registry = registry;
        this._persistence = persistence;
        this._logger = logger;
        _postActionHooks = postActionHooks?.ToArray() ?? Array.Empty<IWorkItemPostActionHook>();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _referenceGenerator = referenceGenerator ?? new ApplicationReferenceGenerator();
        _originStateResolvers =
            originStateResolvers?.ToArray() ?? Array.Empty<IWorkItemOriginStateResolver>();
    }

    public async Task<WorkItemActionResult> SubmitAsync(
        IWorkItemType type,
        BsonDocument payload,
        string? submittedBy,
        ClaimsPrincipal user,
        IReadOnlyDictionary<string, string?>? submissionMetadata = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payload);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // RA-196: ensure 'source' is in the payload. If the caller provided
        // it in the submission metadata (the standard BFF pattern) it is
        // mirrored here so consumers can find it without parsing the audit
        // log. RA-219: 'applicationReference' is NO LONGER caller-supplied —
        // any client-supplied value is ignored; the engine generates it
        // server-side below.
        if (
            submissionMetadata != null
            && submissionMetadata.TryGetValue("source", out var sourceVal)
            && sourceVal != null
            && !payload.Contains("source")
        )
        {
            payload["source"] = sourceVal;
        }

        // RA-311/MBE-3: idempotent submit. The operator backend forwards the
        // operator's original "submit application" call and may retry it
        // after OJ FE's client-side timeout even though the first attempt
        // already succeeded here (CDP logs show a 5s client timeout against
        // a round-trip that can take up to 100s). When the payload carries
        // an operatorApplicationId, a submit for one already on file is
        // treated as a replay: hand back the SAME work item rather than
        // minting a second one. This up-front lookup handles the common
        // case (a genuinely sequential retry) cheaply; the unique + sparse
        // payload.operatorApplicationId index (see WorkItemPersistence)
        // is the backstop for a true concurrent race between two
        // simultaneous requests, handled in the catch clause below.
        var operatorApplicationId =
            payload.TryGetValue("operatorApplicationId", out var operatorApplicationIdValue)
            && operatorApplicationIdValue.IsString
            && !string.IsNullOrWhiteSpace(operatorApplicationIdValue.AsString)
                ? operatorApplicationIdValue.AsString
                : null;

        if (operatorApplicationId is not null)
        {
            var existingByOperatorApplicationId = await _persistence.FindByOperatorApplicationIdAsync(
                type.TypeId, operatorApplicationId, cancellationToken);
            if (existingByOperatorApplicationId is not null)
            {
                _logger.LogInformation(
                    "Submit for operatorApplicationId {OperatorApplicationId} (typeId {TypeId}) is a replay; " +
                        "returning existing work item {WorkItemId} instead of creating a duplicate.",
                    operatorApplicationId,
                    type.TypeId,
                    existingByOperatorApplicationId.Id
                );
                return WorkItemActionResult.IdempotentReplay(existingByOperatorApplicationId);
            }
        }

        var snapshot = WorkItemTemplateSnapshot.Capture(type);

        // RA-219: the backend owns the applicationReference. Generate one
        // server-side and persist; on the (rare) unique-index collision
        // regenerate and retry up to MaxApplicationReferenceAttempts times.
        // The reference is stamped onto both the payload (for consumers /
        // the BFF, at payload.applicationReference) and the birth audit
        // entry on each attempt so a retried reference stays consistent
        // across the document and its audit timeline.
        WorkItem? workItem = null;
        for (var attempt = 1; ; attempt++)
        {
            var applicationReference = _referenceGenerator.Generate(payload, attempt);
            payload["applicationReference"] = applicationReference;

            workItem = new WorkItem
            {
                TypeId = type.TypeId,
                StateId = type.InitialState.Id,
                SubmittedAt = now,
                LastModifiedAt = now,
                SubmittedBy = submittedBy,
                TemplateSnapshot = snapshot,
                TemplateVersion = snapshot.TemplateVersion,
                Payload = payload,
            };

            // Birth event: the audit timeline must start at submission rather
            // than the first state change. Appended to the in-memory list
            // before the single CreateAsync call so the document and its first
            // audit entry land in storage together.
            //
            // RA-126: enrich the birth entry with the originating BFF /
            // application context so audit consumers can reconstruct who
            // submitted from where without joining back to request logs.
            // 'source' is caller-supplied (BFF / operator FE forward it);
            // 'clientId' is the caller-asserted, HMAC-verified client id
            // (see ClientIdAuthenticationHandler); 'userId' is the end-user
            // identity claim required for any mutation. RA-219:
            // 'applicationReference' is the server-generated value.
            AppendAudit(
                workItem,
                "work-item-submitted",
                "Work item submitted",
                user,
                now,
                new()
                {
                    ["typeId"] = type.TypeId,
                    ["stateId"] = workItem.StateId,
                    ["templateVersion"] = snapshot.TemplateVersion,
                    ["source"] =
                        submissionMetadata is not null
                        && submissionMetadata.TryGetValue("source", out var src)
                            ? src
                            : null,
                    ["clientId"] = user.FindFirstValue("client_id"),
                    ["userId"] = ResolveActorUserId(user),
                    ["applicationReference"] = applicationReference,
                }
            );

            _logger.LogInformation(
                "Persisting work item {WorkItemId} typeId={TypeId} applicationReference={ApplicationReference} submittedBy={SubmittedBy} (attempt {Attempt})",
                workItem.Id,
                type.TypeId,
                applicationReference,
                submittedBy,
                attempt
            );

            try
            {
                await _persistence.CreateAsync(workItem, cancellationToken);
                break;
            }
            // RA-311/MBE-3: a genuine race — another request carrying the
            // same operatorApplicationId won the insert between the
            // up-front lookup above and this write. Regenerating a fresh
            // applicationReference and retrying would not help: the
            // collision is on the caller-supplied operatorApplicationId,
            // which never changes between attempts. Look up whatever the
            // winner persisted and hand that back as a replay instead.
            catch (MongoWriteException ex)
                when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey
                    && IsOperatorApplicationIdDuplicate(ex)
                )
            {
                var winner = operatorApplicationId is null
                    ? null
                    : await _persistence.FindByOperatorApplicationIdAsync(
                        type.TypeId, operatorApplicationId, cancellationToken);
                if (winner is not null)
                {
                    _logger.LogInformation(
                        "operatorApplicationId {OperatorApplicationId} (typeId {TypeId}) collided on insert; " +
                            "treating as a submit replay and returning the existing work item {WorkItemId}.",
                        operatorApplicationId,
                        type.TypeId,
                        winner.Id
                    );
                    return WorkItemActionResult.IdempotentReplay(winner);
                }
                // Extremely unlikely: the winning document is gone by the
                // time we look for it. Nothing about retrying with a new
                // applicationReference fixes an operatorApplicationId
                // collision, so propagate rather than loop forever.
                throw;
            }
            // Only retry on a duplicate-key error that is actually the
            // applicationReference unique index. Keying on the DuplicateKey
            // category alone would silently swallow (and misreport as a
            // reference collision) a future unrelated unique index — so we
            // also require the write error to name the applicationReference
            // index; anything else propagates unchanged.
            catch (MongoWriteException ex)
                when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey
                    && IsApplicationReferenceDuplicate(ex)
                )
            {
                if (attempt >= MaxApplicationReferenceAttempts)
                {
                    // RA-219 PR review: surface exhaustion as a structured
                    // failure the endpoint already maps to a clean
                    // ProblemDetails response, rather than throwing past the
                    // endpoint as an unhandled 500.
                    _logger.LogError(
                        ex,
                        "Failed to generate a unique applicationReference after {Attempts} attempts",
                        MaxApplicationReferenceAttempts
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.ApplicationReferenceExhausted,
                        $"Failed to generate a unique applicationReference after "
                            + $"{MaxApplicationReferenceAttempts} attempts."
                    );
                }
                _logger.LogWarning(
                    "applicationReference {ApplicationReference} collided on attempt {Attempt}; regenerating",
                    applicationReference,
                    attempt
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "MongoDB write failed for work item {WorkItemId} typeId={TypeId} applicationReference={ApplicationReference} submittedBy={SubmittedBy}: {ExceptionType}",
                    workItem.Id,
                    type.TypeId,
                    applicationReference,
                    submittedBy,
                    ex.GetType().Name
                );
                throw;
            }
        }

        var persistedReference = payload.Contains("applicationReference")
            ? payload["applicationReference"].AsString
            : null;

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) submitted in state {StateId} applicationReference={ApplicationReference} by {User}",
            workItem!.Id,
            workItem.TypeId,
            workItem.StateId,
            persistedReference,
            DescribeUser(user)
        );

        await InvokeSubmittedHooksAsync(workItem, user, cancellationToken);

        return WorkItemActionResult.Success(workItem);
    }

    /// <summary>
    /// True when a duplicate-key write error was raised by the
    /// <c>payload.applicationReference</c> unique index specifically. The
    /// driver puts the offending index name in the write error message
    /// (e.g. <c>E11000 duplicate key error ... index: payload.applicationReference_1 ...</c>),
    /// so a substring match on the field name is sufficient to tell our
    /// collision apart from any other unique index that might exist now or
    /// in future. Defensive against a null message.
    /// </summary>
    private const string ApplicationReferenceField = "applicationReference";

    private static bool IsApplicationReferenceDuplicate(MongoWriteException ex) =>
        ex.WriteError?.Message?.Contains(
            ApplicationReferenceField,
            StringComparison.OrdinalIgnoreCase
        ) == true;

    /// <summary>
    /// True when a duplicate-key write error was raised by the
    /// <c>payload.operatorApplicationId</c> unique index specifically
    /// (RA-311/MBE-3) — the same substring-match approach as
    /// <see cref="IsApplicationReferenceDuplicate"/>, just against a
    /// different index name.
    /// </summary>
    private const string OperatorApplicationIdField = "operatorApplicationId";

    private static bool IsOperatorApplicationIdDuplicate(MongoWriteException ex) =>
        ex.WriteError?.Message?.Contains(
            OperatorApplicationIdField,
            StringComparison.OrdinalIgnoreCase
        ) == true;

    public async Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
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

        var transition = template!.Transitions.FirstOrDefault(t =>
            string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
        );
        if (transition is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Action '{actionId}' is not declared by work item type '{workItem!.TypeId}'."
            );
        }

        if (TerminalStates.Find(template, workItem!.StateId) is { } currentTerminalState)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{currentTerminalState.Id}'; no actions are allowed."
            );
        }

        if (
            !string.Equals(
                transition.FromStateId,
                workItem!.StateId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{actionId}' moves work items from '{transition.FromStateId}', "
                    + $"but {workItemId} is in '{workItem.StateId}'."
            );
        }

        var previousState = workItem.StateId;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.StateId = transition.ToStateId;
        workItem.LastModifiedAt = now;
        AppendAudit(
            workItem,
            "action-applied",
            "Action applied",
            user,
            now,
            new()
            {
                ["actionId"] = transition.ActionId,
                ["actionDisplayName"] = transition.DisplayName,
                ["fromStateId"] = previousState,
                ["toStateId"] = workItem.StateId,
            }
        );
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
            workItem.Id,
            workItem.TypeId,
            previousState,
            workItem.StateId,
            transition.ActionId,
            DescribeUser(user)
        );

        await InvokeActionAppliedHooksAsync(
            workItem,
            transition.ActionId,
            previousState,
            user,
            cancellationToken
        );

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> AssignAsync(
        Guid workItemId,
        string assigneeId,
        string? assigneeName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (string.IsNullOrWhiteSpace(assigneeId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidAssignment,
                "Assignee id is required."
            );
        }

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (RequireNonTerminalState(workItem, "assigned") is { } terminalFailure)
        {
            return terminalFailure;
        }

        var trimmedAssigneeId = assigneeId.Trim();
        var actorUserId = ResolveActorUserId(user);

        // RA-323: every caseworker may assign to anyone, including
        // re-assigning an already-assigned item — there is no permission
        // tier to check here any more.

        var snapshotName = string.IsNullOrWhiteSpace(assigneeName) ? null : assigneeName.Trim();
        var alreadyAssignedToSameUser =
            string.Equals(workItem.AssignedToId, trimmedAssigneeId, StringComparison.Ordinal)
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
        AppendAudit(
            workItem,
            "assigned",
            "Assigned",
            user,
            workItem.AssignedAt.Value,
            new()
            {
                ["assigneeId"] = trimmedAssigneeId,
                ["assigneeName"] = snapshotName,
                ["previousAssigneeId"] = previousAssigneeId,
                ["previousAssigneeName"] = previousAssigneeName,
            }
        );
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
            workItem.Id,
            workItem.TypeId,
            previousAssigneeId ?? "(unassigned)",
            trimmedAssigneeId,
            DescribeUser(user)
        );

        // RA-237: assignment is a first-class envelope operation, so it must
        // fan out to the post-action hooks explicitly (ApplyActionAsync's hook
        // fan-out does not cover it). A brand-new assignment on a previously
        // unassigned item is Assigned; changing the assignee of an
        // already-assigned item is Reassigned. Fires only on this real-change
        // path — the earlier IdempotentReplay return skips it.
        var change = previousAssigneeId is null
            ? WorkItemAssignmentChange.Assigned
            : WorkItemAssignmentChange.Reassigned;
        await InvokeAssignmentChangedHooksAsync(workItem, change, user, cancellationToken);

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> UnassignAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
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
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (RequireNonTerminalState(workItem, "unassigned") is { } terminalFailure)
        {
            return terminalFailure;
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
        AppendAudit(
            workItem,
            "unassigned",
            "Unassigned",
            user,
            now,
            new()
            {
                ["previousAssigneeId"] = previousAssigneeId,
                ["previousAssigneeName"] = previousAssigneeName,
            }
        );
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
            workItem.Id,
            workItem.TypeId,
            previousAssigneeId,
            DescribeUser(user)
        );

        // RA-237: notify the post-action hooks of the real unassignment.
        // The earlier IdempotentReplay return (already unassigned) skips this.
        await InvokeAssignmentChangedHooksAsync(
            workItem, WorkItemAssignmentChange.Unassigned, user, cancellationToken);

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
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (ValidateNoteText(text, out var trimmed, out var noteFailure) is false)
        {
            return noteFailure!;
        }

        // Work-item-level note (RA-96): a single GetByIdAsync, no template
        // resolution required, so the note still works for legacy items
        // whose module is no longer registered and which carry no
        // template snapshot.
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        var note = new WorkItemNote
        {
            Text = trimmed!,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = ResolveActorUserId(user)!,
            CreatedByName = user?.FindFirstValue("user:name"),
        };
        workItem.Notes.Add(note);
        workItem.LastModifiedAt = note.CreatedAt;

        AppendAudit(
            workItem,
            "note-added",
            "Note added",
            user,
            note.CreatedAt,
            new()
            {
                ["noteId"] = note.Id.ToString(),
                // Snapshot the trimmed body so the audit log is self-describing —
                // a reader does not need to cross-reference Notes by id to see
                // what was written. Already capped by MaxNoteLength.
                ["noteText"] = note.Text,
            }
        );

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Note {NoteId} added to work item {WorkItemId} ({TypeId}) by {User}",
            note.Id,
            workItem.Id,
            workItem.TypeId,
            DescribeUser(user)
        );

        return WorkItemActionResult.Success(workItem);
    }

    private static bool ValidateNoteText(
        string text,
        out string? trimmed,
        out WorkItemActionResult? failure
    )
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            trimmed = null;
            failure = WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                "Note text is required."
            );
            return false;
        }

        var t = text.Trim();
        if (t.Length > MaxNoteLength)
        {
            trimmed = null;
            failure = WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                $"Note text must be {MaxNoteLength} characters or fewer."
            );
            return false;
        }

        trimmed = t;
        failure = null;
        return true;
    }

    public async Task<WorkItemEngineProjection?> ProjectAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default
    )
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
            // no available actions so callers can still display it.
            return new WorkItemEngineProjection(
                workItem,
                ResolveTemplateVersion(workItem),
                Array.Empty<WorkItemTransition>(),
                // No template means no resolver could have been consulted, so
                // the item is reported as its own origin.
                workItem.StateId
            );
        }

        var isTerminal = TerminalStates.Find(template, workItem.StateId) is not null;

        // Available actions are matched on the item's ACTUAL state, never its
        // origin state. Where an item can move next is a different question
        // from where it came in from: an item in 'updated' reports the
        // originating state via OriginStateId but only ever offers the
        // transitions that genuinely leave 'updated'. Matching on the origin
        // would let a caller invoke an action from a state the item is not in.
        IReadOnlyCollection<WorkItemTransition> available = isTerminal
            ? Array.Empty<WorkItemTransition>()
            : template
                .Transitions.Where(t =>
                    string.Equals(
                        t.FromStateId,
                        workItem.StateId,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                // RA-364: never offer an action the generic action endpoint
                // would reject. Transitions declared CallerInvocable: false
                // are reachable only through a module's own bespoke service
                // resolving them server-side (re-accreditation's
                // resume-during-* / continue-review-during-*, and since RA-410
                // submit-for-decision / reject), so listing them here
                // advertised buttons that always failed — and, because each of
                // those four shares a DisplayName and a FromStateId, rendered
                // as four identical dead controls. This mirrors the guard in
                // WorkItemEndpoints.Action; the bespoke services are
                // unaffected because they call ApplyActionAsync directly and
                // never consult AvailableActions.
                .Where(t => t.CallerInvocable)
                .ToList();

        return new WorkItemEngineProjection(
            workItem,
            template.TemplateVersion,
            available,
            ResolveOriginStateId(workItem, template)
        );
    }

    private async Task<(
        WorkItem? WorkItem,
        IWorkItemTemplate? Template,
        WorkItemActionResult? Failure
    )> LoadAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return (
                null,
                null,
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.WorkItemNotFound,
                    $"No work item exists with id '{workItemId}'."
                )
            );
        }

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            return (
                workItem,
                null,
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Work item {workItemId} references unregistered type '{workItem.TypeId}' and has no stored template snapshot."
                )
            );
        }

        return (workItem, template, null);
    }

    /// <summary>
    /// Pick the template the engine should reason about for a work item.
    /// Delegates to <see cref="WorkItemEngineRules.ResolveTemplate"/> so
    /// bespoke module services resolve the same template the engine does.
    /// </summary>
    private IWorkItemTemplate? ResolveTemplate(WorkItem workItem) =>
        WorkItemEngineRules.ResolveTemplate(workItem, _registry);

    /// <summary>
    /// RA-358: refuse an envelope mutation on a closed case. Assignment and
    /// unassignment are the two operations that reach a work item without
    /// going through <see cref="ApplyActionAsync"/>, so before this check a
    /// direct POST could still hand a withdrawn / approved / rejected item to
    /// someone (the front end only hid the buttons). Terminality comes from
    /// the item's own template via <see cref="TerminalStates.Find"/> — no
    /// state id is hardcoded here, and an in-flight item is judged by the
    /// template version it was submitted under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This deliberately reverses RA-295 AC03, which required assignment to
    /// stay available "all the way through" and is the reason no gate existed.
    /// That was about hand-over: keeping a closed case reassignable so it can
    /// be passed to someone else after the fact. RA-358 trades that capability
    /// away — confirmed with the product owner on 2026-08-04, for every
    /// terminal state rather than withdrawal alone. If hand-over on closed
    /// cases is ever wanted back, it needs a route that is explicitly about
    /// hand-over, not an unguarded assign endpoint.
    /// </para>
    /// <para>
    /// Fails <em>closed</em> when the template cannot be resolved at all,
    /// matching <see cref="ApplyActionAsync"/>, which refuses the same
    /// condition before it ever reaches its terminal check. Otherwise a legacy
    /// pre-snapshot item sitting in a terminal state would become freely
    /// assignable the moment its type was de-registered or renamed — the very
    /// hole this guard exists to close, reached through a different door.
    /// </para>
    /// </remarks>
    /// <param name="operation">
    /// Past-tense verb naming the refused mutation, e.g. <c>"assigned"</c>.
    /// </param>
    private WorkItemActionResult? RequireNonTerminalState(WorkItem workItem, string operation)
    {
        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            _logger.LogWarning(
                "Work item {WorkItemId} refused {Operation}: type {TypeId} is not registered "
                    + "and the item has no template snapshot, so terminality cannot be determined",
                workItem.Id,
                operation,
                workItem.TypeId
            );

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                "This case cannot be changed because its type is no longer available."
            );
        }

        // A state the template does not declare carries no terminal metadata,
        // so there is nothing to fail closed on: it is not evidence the case is
        // closed, only that the state is unmodelled. Kept permissive so a
        // mis-seeded state id does not silently freeze assignment; the
        // unresolvable-template case above is the one that hides real terminal
        // states behind an absence.
        if (TerminalStates.Find(template, workItem.StateId) is not { } terminal)
        {
            return null;
        }

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) refused {Operation}: terminal state {StateId}",
            workItem.Id,
            workItem.TypeId,
            operation,
            terminal.Id
        );

        // RA-358: the BFF renders ProblemDetails.detail verbatim in a
        // user-facing error banner, so the message must name the case in
        // human terms only. Deliberately no work item id here — removing the
        // system-generated GUID from user-visible errors is the point of this
        // story. The id stays in the log line above for support.
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.TerminalState,
            $"This case has been {terminal.DisplayName.ToLowerInvariant()} "
                + $"and can no longer be {operation}."
        );
    }

    private string ResolveTemplateVersion(WorkItem workItem) =>
        workItem.TemplateVersion
        ?? workItem.TemplateSnapshot?.TemplateVersion
        ?? _registry.Find(workItem.TypeId)?.TemplateVersion
        ?? "unknown";

    /// <summary>
    /// RA-410: the state <paramref name="workItem"/> returns to when its
    /// current waypoint discharges. Normally the state the item is in, but a
    /// module may name a different one via an
    /// <see cref="IWorkItemOriginStateResolver"/> — see that interface for
    /// why. The first resolver with an opinion wins; a resolver that returns
    /// null (or names a state the template does not declare) is ignored, so an
    /// abstaining or stale resolver degrades to reporting the item's own
    /// state rather than reporting a state it was never in.
    ///
    /// This is a read projection only. Nothing about where the item actually
    /// *is* — which actions are available, terminality, the transition
    /// from-state guard — is resolved through here.
    /// </summary>
    private string ResolveOriginStateId(WorkItem workItem, IWorkItemTemplate template)
    {
        foreach (var resolver in _originStateResolvers)
        {
            // Scope by type before consulting. Without this every resolver
            // would see every item, and "first non-null wins" would make the
            // outcome depend on module registration order — not something any
            // contract promises.
            if (
                !string.Equals(resolver.TypeId, workItem.TypeId, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            string? resolved;
            try
            {
                resolved = resolver.ResolveOriginStateId(workItem, template);
            }
            catch (Exception ex)
            {
                // A resolver is consulted on every read, so a buggy one must
                // not take the worklist down with it. Fall back to the item's
                // real state, which is what the engine did before RA-372.
                _logger.LogError(
                    ex,
                    "Origin state resolver {ResolverType} failed for work item {WorkItemId}; "
                        + "falling back to its actual state {StateId}",
                    resolver.GetType().FullName,
                    workItem.Id,
                    workItem.StateId
                );
                continue;
            }

            if (string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            if (
                !template.States.Any(s =>
                    string.Equals(s.Id, resolved, StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                _logger.LogWarning(
                    "Origin state resolver {ResolverType} returned state '{ResolvedStateId}' for work "
                        + "item {WorkItemId}, which its template does not declare; ignoring",
                    resolver.GetType().FullName,
                    resolved,
                    workItem.Id
                );
                continue;
            }

            return resolved;
        }

        return workItem.StateId;
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
        Dictionary<string, string?> details
    )
    {
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = action,
                ActionDisplayName = actionDisplayName,
                Details = details,
                CreatedAt = createdAt,
                CreatedBy = ResolveActorUserId(user)!,
                CreatedByName = user?.FindFirstValue("user:name"),
            }
        );
    }

    private static WorkItemActionResult ConcurrencyConflict(Guid workItemId) =>
        WorkItemActionResult.Failure(
            WorkItemActionFailureCode.ConcurrencyConflict,
            $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
        );

    private static string DescribeUser(ClaimsPrincipal? user) =>
        user?.FindFirstValue("user:id")
        ?? user?.FindFirstValue("client_id")
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
            "Mutating this work item requires an authenticated end user; "
                + "the request did not include a 'user:id' claim."
        );
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
        CancellationToken cancellationToken
    )
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnSubmittedAsync(workItem, user, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Post-action submit hook {HookType} failed for work item {WorkItemId}",
                    hook.GetType().FullName,
                    workItem.Id
                );
            }
        }
    }

    private async Task InvokeActionAppliedHooksAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(
                    workItem,
                    actionId,
                    fromStateId,
                    user,
                    cancellationToken
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Post-action transition hook {HookType} failed for work item {WorkItemId} action {ActionId}",
                    hook.GetType().FullName,
                    workItem.Id,
                    actionId
                );
            }
        }
    }

    /// <summary>
    /// RA-237: fan out to every registered <see cref="IWorkItemPostActionHook"/>
    /// after a successful, non-idempotent assignment change. Mirrors the
    /// swallow-and-log contract of the submit / action fan-outs so a
    /// misbehaving hook (e.g. a Notify outage in the assignment-email hook)
    /// cannot unwind the assignment that has already persisted.
    /// </summary>
    private async Task InvokeAssignmentChangedHooksAsync(
        WorkItem workItem,
        WorkItemAssignmentChange change,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnAssignmentChangedAsync(workItem, change, user, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Post-action assignment hook {HookType} failed for work item {WorkItemId} change {Change}",
                    hook.GetType().FullName, workItem.Id, change);
            }
        }
    }

}
