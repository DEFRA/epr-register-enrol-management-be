using System.Globalization;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-495: completes duly making for a Standard-workflow work item: the
/// <c>submitted → duly-made</c> transition with the payment date the caseworker
/// entered. <paramref name="paymentDate"/> is already validated
/// (<see cref="StandardDulyMakingValidator"/>, at the endpoint).
/// </summary>
internal interface IStandardDulyMakingService
{
    Task<WorkItemActionResult> CompleteDulyMakingAsync(
        Guid workItemId,
        DateOnly paymentDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// RA-495: duly making shared by every type that opts in to the
/// Standard-workflow lifecycle (<see cref="IStandardLifecycleStrategy"/>);
/// re-accreditation's RA-316 service, moved here unchanged except that the
/// type check is now "has the type opted in" and the payload stamp is the
/// type's own (<see cref="IStandardDulyMakingRules.TryStampPaymentDate"/>).
///
/// RA-316: replaces the task-driven auto-transition hook that previously carried an
/// application from <c>submitted</c> to <c>duly-made</c> when its two checklist
/// tasks were ticked. Duly making is now an explicit regulator action that
/// captures a payment date, because the 12-week SLA must run from when the
/// operator paid — not from the moment the regulator got round to recording it
/// (AC06). A checklist has nowhere to put that date.
///
/// On a single <see cref="IWorkItemPersistence.ReplaceAsync"/> the service:
/// <list type="bullet">
///   <item>Validates the work item exists, is of an opted-in type, is not
///   terminal, and is somewhere duly making can legitimately start from.</item>
///   <item>Discharges the <c>updated</c> waypoint first when applicable — see
///   <see cref="DischargeUpdatedWaypoint"/>.</item>
///   <item>Stamps <c>paymentDate</c> on the payload and moves
///   <see cref="WorkItem.StateId"/> to <c>duly-made</c>.</item>
///   <item>Starts the SLA clock with
///   <see cref="WorkItemSlaClock.StartedAt"/> anchored to midnight UTC of the
///   entered payment date.</item>
///   <item>Appends the audit entries: the <c>action-applied</c> entry for
///   <c>duly-make</c> (plus one for the waypoint discharge when it happened)
///   and <c>sla-clock-started</c>.</item>
/// </list>
///
/// On a <see cref="WorkItemConcurrencyException"/> the operation is retried by
/// reloading the latest document and re-running validation, up to
/// <see cref="MaxAttempts"/> times — the same pattern
/// re-accreditation's approval service uses.
///
/// After the write succeeds the registered <see cref="IWorkItemPostActionHook"/>s
/// are invoked with action id <c>duly-make</c>, which is what fires the type's
/// own side effects: for re-accreditation the DulyMade operator email (AC07),
/// and for any type with a push target the status push to the operator
/// journey (AC09).
/// </summary>
internal sealed class StandardDulyMakingService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IEnumerable<IWorkItemPostActionHook> postActionHooks,
    TimeProvider timeProvider,
    ILogger logger,
    StandardLifecycleStrategies<IStandardDulyMakingRules> strategies
) : IStandardDulyMakingService
{
    private const int MaxAttempts = 3;
    private const string FromStateId = "submitted";
    private const string ToStateId = "duly-made";
    private const string ActionId = "duly-make";
    private const string ActionDisplayName = "Duly make";
    private const string ContinueReviewActionId = "continue-review-during-duly-making";

    private readonly IWorkItemPostActionHook[] _postActionHooks = postActionHooks.ToArray();

    public async Task<WorkItemActionResult> CompleteDulyMakingAsync(
        Guid workItemId,
        DateOnly paymentDate,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        WorkItem? dulyMade = null;
        var dischargedWaypoint = false;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var loaded = await LoadAsync(workItemId, cancellationToken);
            if (loaded.Failure is { } loadFailure)
            {
                return loadFailure;
            }

            var workItem = loaded.WorkItem!;
            var starting = CheckStartingPoint(workItem, loaded.Template!);
            if (starting.Failure is { } startFailure)
            {
                return startFailure;
            }

            if (
                ApplyDulyMake(
                    workItem,
                    loaded.Strategy!,
                    paymentDate,
                    user,
                    starting.NeedsWaypointDischarge
                ) is
                { } applyFailure
            )
            {
                return applyFailure;
            }

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
                dulyMade = workItem;
                dischargedWaypoint = starting.NeedsWaypointDischarge;
                break;
            }
            catch (WorkItemConcurrencyException ex)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(
                        ex,
                        "Duly making of work item {WorkItemId} abandoned after {Attempts} attempts "
                            + "due to repeated concurrency conflicts.",
                        workItemId,
                        MaxAttempts
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.ConcurrencyConflict,
                        $"Work item '{workItemId}' was modified concurrently. "
                            + "Reload the work item and retry."
                    );
                }
            }
        }

        // The loop has either returned or assigned `dulyMade`; the compiler
        // cannot see the latter, so narrow with an assertion.
        var persisted = dulyMade!;

        logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) duly made by {UserId} with payment date "
                + "{PaymentDate}; SLA clock anchored to that date{WaypointDischarge}.",
            persisted.Id,
            persisted.TypeId,
            user.FindFirstValue("user:id"),
            paymentDate.ToString("yyyy-MM-dd"),
            dischargedWaypoint
                ? ", having first left the 'updated' waypoint via continue-review-during-duly-making"
                : string.Empty
        );

        // The state change bypassed WorkItemService.ApplyActionAsync, so the
        // generic engine's hook fan-out never ran. Invoke it here: this is what
        // sends the DulyMade operator email (AC07) and pushes the new status to
        // the operator backend (AC09). It happens strictly after the save, so a
        // hook that writes its own audit entry cannot collide with our write.
        await InvokeActionAppliedHooksAsync(persisted, ActionId, FromStateId, user);

        return WorkItemActionResult.Success(persisted);
    }

    /// <summary>
    /// One attempt's freshly loaded inputs: the work item, its opted-in
    /// strategy and the template it is evaluated under, or the failure to
    /// hand back instead.
    /// </summary>
    private readonly record struct Loaded(
        WorkItemActionResult? Failure,
        WorkItem? WorkItem,
        IStandardDulyMakingRules? Strategy,
        IWorkItemTemplate? Template
    );

    private async Task<Loaded> LoadAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

        if (workItem is null)
        {
            return new Loaded(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.WorkItemNotFound,
                    $"No work item exists with id '{workItemId}'."
                ),
                null,
                null,
                null
            );
        }

        if (strategies.Find(workItem.TypeId) is not { } strategy)
        {
            return new Loaded(strategies.NotOptedIn(workItemId, workItem.TypeId), null, null, null);
        }

        var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
        if (template is null)
        {
            // Not a type mismatch (the type has opted in), so not
            // UnknownAction, which the endpoints label wrong-work-item-type.
            return new Loaded(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.TemplateUnavailable,
                    $"Work item {workItemId} references unregistered type '{workItem.TypeId}' "
                        + "and has no stored template snapshot."
                ),
                null,
                null,
                null
            );
        }

        return new Loaded(null, workItem, strategy, template);
    }

    /// <summary>
    /// Whether duly making may start from where the item is, and whether the
    /// <c>updated</c> waypoint must be discharged first.
    /// </summary>
    private readonly record struct StartingPoint(WorkItemActionResult? Failure, bool NeedsWaypointDischarge);

    private StartingPoint CheckStartingPoint(WorkItem workItem, IWorkItemTemplate template)
    {
        var workItemId = workItem.Id;

        if (IsTerminal(template, workItem.StateId))
        {
            return new StartingPoint(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.TerminalState,
                    $"Work item {workItemId} is in terminal state '{workItem.StateId}'; "
                        + "no actions are allowed."
                ),
                false
            );
        }

        // Two legitimate starting points, and only two.
        //
        //  1. 'submitted' — the ordinary path.
        //  2. 'updated' — but ONLY when the query that put it there was
        //     raised during duly making. An item in 'updated' having been
        //     queried from assessment or decision is mid-review; duly making
        //     it would skip whole stages.
        //
        // ResolveOriginatingStateId reads the item's own audit history and
        // resolves it through the item's own frozen template, so an item
        // whose snapshot lacks the continue-review edge yields null and is
        // refused here rather than being carried across an edge its
        // template does not declare.
        var needsWaypointDischarge = false;
        if (!string.Equals(workItem.StateId, FromStateId, StringComparison.OrdinalIgnoreCase))
        {
            var originStateId = string.Equals(workItem.StateId, StandardUpdatedOrigin.StateId, StringComparison.OrdinalIgnoreCase)
                ? StandardUpdatedOrigin.ResolveOriginatingStateId(workItem, template)
                : null;

            if (!string.Equals(originStateId, FromStateId, StringComparison.OrdinalIgnoreCase))
            {
                return new StartingPoint(
                    WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.InvalidTransition,
                        $"Action '{ActionId}' moves work items from '{FromStateId}', "
                            + $"but {workItemId} is in '{workItem.StateId}'."
                    ),
                    false
                );
            }

            needsWaypointDischarge = true;
        }

        // The duly-make transition must be declared by the item's OWN
        // template, not merely by the live type. Template versioning is the
        // framework's hard rule: an in-flight v10 item is evaluated under
        // v10 rules. A snapshot migration (re-accreditation: ReAccreditationDulyMakeSnapshotMigration) adds the
        // transition to every pre-v11 snapshot at startup and retries on
        // each boot until it succeeds, so this refusal is a transient
        // "migration has not caught up yet", never a permanent dead end.
        if (
            !template.Transitions.Any(t =>
                string.Equals(t.ActionId, ActionId, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            logger.LogWarning(
                "Work item {WorkItemId} cannot be duly made: its template snapshot "
                    + "({TemplateVersion}) does not declare the '{ActionId}' transition. "
                    + "Its snapshot migration has not yet patched it.",
                workItem.Id,
                template.TemplateVersion,
                ActionId
            );
            return new StartingPoint(
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Work item '{workItemId}' was submitted under template version "
                        + $"'{template.TemplateVersion}', which does not support the "
                        + $"'{ActionId}' action. Retry shortly; if this persists, the "
                        + "snapshot migration needs investigating."
                ),
                false
            );
        }

        return new StartingPoint(null, needsWaypointDischarge);
    }

    /// <summary>
    /// Everything below mutates the loaded instance in memory and is
    /// committed by the SINGLE ReplaceAsync the caller makes. That is a hard
    /// constraint inherited from the hook this service replaces, not a
    /// style choice: ReplaceAsync is guarded by an optimistic-concurrency
    /// check on WorkItem.Version, and anything that writes out of band
    /// between our load and our save (an audit append, a status push)
    /// advances the stored version and makes our save throw. Splitting
    /// the waypoint discharge into its own write is what previously left
    /// applications stranded in 'submitted' behind a 500.
    ///
    /// Returns the failure to hand back, or null once the item is ready to save.
    /// </summary>
    private WorkItemActionResult? ApplyDulyMake(
        WorkItem workItem,
        IStandardDulyMakingRules strategy,
        DateOnly paymentDate,
        ClaimsPrincipal user,
        bool needsWaypointDischarge
    )
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (needsWaypointDischarge)
        {
            DischargeUpdatedWaypoint(workItem, user, now);
        }

        if (!strategy.TryStampPaymentDate(workItem, paymentDate, logger))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item '{workItem.Id}' payload is corrupt and cannot be read. "
                    + "Inspect the server logs for details; a manual data repair may be required."
            );
        }

        var previousState = workItem.StateId;
        workItem.StateId = ToStateId;
        workItem.LastModifiedAt = now;

        // AC06: the clock is anchored to the entered payment date, NOT to
        // now. Midnight UTC of that date is the earliest instant consistent
        // with it, which keeps the regulator's 12 weeks from being
        // shortened by whatever part of the day the payment landed in.
        var slaStartedAt = paymentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var slaClock = new WorkItemSlaClock { StartedAt = slaStartedAt };
        workItem.SlaClock = slaClock;

        AppendAudit(
            workItem,
            "action-applied",
            "Action applied",
            user,
            now,
            new Dictionary<string, string?>
            {
                ["actionId"] = ActionId,
                ["actionDisplayName"] = ActionDisplayName,
                ["fromStateId"] = previousState,
                ["toStateId"] = workItem.StateId,
                // AC08: the entered date is part of the auditable record of
                // what the regulator did, not just an input to the clock.
                ["paymentDate"] = paymentDate.ToString("yyyy-MM-dd"),
            }
        );
        AppendAudit(
            workItem,
            "sla-clock-started",
            "SLA clock started",
            user,
            now,
            new Dictionary<string, string?>
            {
                ["startedAt"] = slaStartedAt.ToString("O"),
                ["targetDays"] = slaClock.TargetDuration.TotalDays.ToString(CultureInfo.InvariantCulture),
                // Stated explicitly so the audit trail shows the clock was
                // back-dated on purpose rather than looking like a bug to
                // whoever reads it months later.
                ["anchoredTo"] = "payment-date",
            }
        );

        return null;
    }

    /// <summary>
    /// Carry a work item out of the <c>updated</c> waypoint via its declared
    /// <c>continue-review-during-duly-making</c> transition, so the duly-made
    /// transition that follows starts from <c>submitted</c> — a from-state every
    /// downstream consumer already models.
    ///
    /// The alternative, jumping <c>updated → duly-made</c> directly, traverses
    /// an edge the Standard workflow does not declare. Nothing would
    /// validate it, and <c>updated</c> would land in the audit trail and go on
    /// the wire to the operator backend as a from/to pair neither
    /// management-fe nor the journey tests model, because it is not in the
    /// template both of them mirror.
    ///
    /// The action id is fixed rather than derived because the caller has already
    /// established that this item's origin state is <c>submitted</c>, which is
    /// precisely the pairing this transition describes.
    ///
    /// Mutates in memory only — the caller commits this together with the
    /// duly-made transition in one write. See the note at the call site for why
    /// a separate write here is not an option.
    ///
    /// A consequence worth naming: this transition is not pushed to the operator
    /// backend on its own. The push reads the destination state off
    /// <see cref="WorkItem.StateId"/> and must run after the save, by which
    /// point the item is already <c>duly-made</c> — so pushing it separately
    /// could only report it as ending somewhere it did not. The operator backend
    /// sees the one push it can act on, <c>duly-make (submitted → duly-made)</c>.
    /// The full two-step path stays visible in the audit log, which is the
    /// regulator-facing record.
    /// </summary>
    private static void DischargeUpdatedWaypoint(
        WorkItem workItem,
        ClaimsPrincipal user,
        DateTime now
    )
    {
        var fromStateId = workItem.StateId;
        workItem.StateId = FromStateId;
        workItem.LastModifiedAt = now;
        AppendAudit(
            workItem,
            "action-applied",
            "Action applied",
            user,
            now,
            new Dictionary<string, string?>
            {
                ["actionId"] = ContinueReviewActionId,
                // Mirrors the DisplayName the Standard workflow declares for this
                // transition, matching how the duly-make entry states its own
                // display name inline.
                ["actionDisplayName"] = "Continue review",
                ["fromStateId"] = fromStateId,
                ["toStateId"] = workItem.StateId,
            }
        );
    }

    private static bool IsTerminal(IWorkItemTemplate template, string stateId) =>
        template.States.Any(s =>
            string.Equals(s.Id, stateId, StringComparison.OrdinalIgnoreCase) && s.IsTerminal
        );

    private Task InvokeActionAppliedHooksAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user
    ) =>
        // A notification or push failure must never unwind a duly making that
        // is already committed to the database.
        StandardLifecycleAudit.InvokeActionAppliedHooksAsync(
            _postActionHooks,
            workItem,
            actionId,
            fromStateId,
            user,
            logger
        );

    private static void AppendAudit(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
        DateTime createdAt,
        Dictionary<string, string?> details
    ) => StandardLifecycleAudit.Append(workItem, action, actionDisplayName, user, createdAt, details);

    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user) =>
        StandardLifecycleAudit.RequireActorIdentity(user);
}
