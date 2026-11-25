using System.Globalization;
using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using MongoDB.Bson;

namespace EprRegisterEnrolManagementBe.WorkItems.Workflows.StandardLifecycle;

/// <summary>
/// RA-800: corrects the valid-from date of a Standard-workflow case that has been
/// approved and is awaiting applicant contact. The date and reason are already
/// shape-validated (<see cref="StandardChangeValidFromValidator"/>, at the
/// endpoint); the service applies the rules that need the work item.
/// </summary>
internal interface IStandardChangeValidFromService
{
    Task<WorkItemActionResult> ChangeValidFromAsync(
        Guid workItemId,
        DateOnly newDate,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// RA-800: change valid from date, for a type that opts in to the lifecycle and
/// takes a valid-from date (<see cref="IStandardLifecycleStrategy.TakesEffectiveDate"/>,
/// add-ORS). Modelled on RA-576's change payment date: one version-guarded
/// <see cref="IWorkItemPersistence.ReplaceAsync"/> rewrites <c>payload.effectiveDate</c>
/// and appends a dedicated <c>valid-from-changed</c> audit entry. It does NOT touch
/// <see cref="WorkItem.StateId"/> or <see cref="WorkItem.SlaClock"/>, runs no
/// post-action hooks and pushes nothing to the operator: Confirm applicant contact
/// pushes the stored date, so the correction flows through then.
///
/// Allowed only in <c>approved-pending-contact</c> (step 1 recorded, contact not
/// yet confirmed) with a stored date; every other state is a conflict. Concurrency
/// conflicts reload and re-check, up to <see cref="MaxAttempts"/> times.
/// </summary>
internal sealed class StandardChangeValidFromService(
    IWorkItemPersistence persistence,
    StandardLifecycleStrategies strategies,
    TimeProvider timeProvider,
    ILogger logger
) : IStandardChangeValidFromService
{
    private const int MaxAttempts = 3;

    // A dedicated audit action, not `action-applied`: this is not a state
    // transition (same reasoning as `payment-date-changed`).
    public const string AuditAction = "valid-from-changed";
    private const string AuditActionDisplayName = "Valid from date changed";

    public async Task<WorkItemActionResult> ChangeValidFromAsync(
        Guid workItemId,
        DateOnly newDate,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        if (StandardLifecycleAudit.RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);
            if (Check(workItemId, workItem, newDate) is { } refusal)
            {
                return refusal;
            }

            var previous = StandardEffectiveDate.ReadStamped(workItem!)!.Value;
            Apply(workItem!, previous, newDate, reason, user);

            try
            {
                await persistence.ReplaceAsync(workItem!, cancellationToken);
            }
            catch (WorkItemConcurrencyException) when (attempt < MaxAttempts)
            {
                continue;
            }
            catch (WorkItemConcurrencyException ex)
            {
                logger.LogError(
                    ex,
                    "Valid-from change on work item {WorkItemId} abandoned after {Attempts} attempts due to "
                        + "repeated concurrency conflicts.",
                    workItemId,
                    MaxAttempts
                );
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.ConcurrencyConflict,
                    $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
                );
            }

            logger.LogInformation(
                "Valid-from date of work item {WorkItemId} changed by {UserId} from {Previous} to {New}.",
                workItemId,
                user.FindFirstValue("user:id"),
                Format(previous),
                Format(newDate)
            );
            return WorkItemActionResult.Success(workItem!);
        }

        // Unreachable: the final attempt either returns or fails with a conflict.
        throw new InvalidOperationException("Valid-from change loop exited unexpectedly.");
    }

    /// <summary>The refusal for this item as it is now, or null when the change may be written.</summary>
    private WorkItemActionResult? Check(Guid workItemId, WorkItem? workItem, DateOnly newDate)
    {
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        var strategy = strategies.Find(workItem.TypeId);
        if (strategy is null)
        {
            return strategies.NotOptedIn(workItemId, workItem.TypeId);
        }

        if (!strategy.TakesEffectiveDate)
        {
            // Mapped to a 400 wrong-work-item-type, as for a type that has not opted in.
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Work item {workItemId} is of type '{workItem.TypeId}', which does not take a valid-from date."
            );
        }

        if (
            Is(workItem.StateId, StandardWorkflow.ApprovedStateId)
            || Is(workItem.StateId, StandardWorkflow.RejectedStateId)
            || Is(workItem.StateId, StandardWorkflow.WithdrawnStateId)
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{workItem.StateId}'; its valid-from date cannot change."
            );
        }

        if (!Is(workItem.StateId, StandardWorkflow.ApprovedPendingContactStateId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                "The valid-from date can only be changed for a work item in "
                    + $"'{StandardWorkflow.ApprovedPendingContactStateId}', but {workItemId} is in '{workItem.StateId}'."
            );
        }

        if (StandardEffectiveDate.ReadStamped(workItem) is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Work item {workItemId} has no valid-from date to change."
            );
        }

        return StandardEffectiveDate.CheckWithinAccreditationYear(workItem, newDate);
    }

    /// <summary>Mutates the loaded instance in memory only; the caller commits it with one write.</summary>
    private void Apply(WorkItem workItem, DateOnly previous, DateOnly newDate, string reason, ClaimsPrincipal user)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var payload = (workItem.Payload ?? new BsonDocument()).DeepClone().AsBsonDocument;
        payload[StandardEffectiveDate.Field] = Format(newDate);
        workItem.ReplacePayload(payload);
        workItem.LastModifiedAt = now;

        StandardLifecycleAudit.Append(
            workItem,
            AuditAction,
            AuditActionDisplayName,
            user,
            now,
            new Dictionary<string, string?>
            {
                ["previousEffectiveDate"] = Format(previous),
                [StandardEffectiveDate.Field] = Format(newDate),
                ["reason"] = reason,
            }
        );
    }

    private static string Format(DateOnly date) =>
        date.ToString(StandardEffectiveDate.DateFormat, CultureInfo.InvariantCulture);

    private static bool Is(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
