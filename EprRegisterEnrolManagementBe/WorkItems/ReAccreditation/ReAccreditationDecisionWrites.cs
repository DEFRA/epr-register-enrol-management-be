using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-527: the pieces of a determination write shared by
/// <see cref="ReAccreditationApprovalService"/> and
/// <see cref="ReAccreditationRefusalService"/> — each step of either outcome
/// is one <see cref="IWorkItemPersistence.ReplaceAsync"/> call that appends its
/// own audit entries, optionally updates the payload, and then runs the
/// post-action hooks.
/// </summary>
internal static class ReAccreditationDecisionWrites
{
    /// <summary>
    /// Appends an audit entry to <paramref name="workItem"/> in memory, to be
    /// persisted by the caller's single write. Call it AFTER any state change:
    /// the entry snapshots the item's state as of this event.
    /// </summary>
    public static void AppendAudit(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
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
                CreatedBy = user.FindFirstValue("user:id"),
                CreatedByName = user.FindFirstValue("user:name"),
                // epr-rr9s: snapshot the state as of this event.
                StateId = workItem.StateId,
            }
        );
    }

    /// <summary>
    /// The work item's payload as the model, or null (logged) when the stored
    /// payload cannot be read. Writing over an unreadable payload would destroy
    /// existing data (organisation name, registration number, nation, etc.), so
    /// a null here must refuse the write. RA-527 task 007: also used by
    /// <see cref="ReAccreditationConfirmContactGuard"/> before the push.
    /// </summary>
    public static ReAccreditationPayload? TryReadPayload(WorkItem workItem, ILogger? logger)
    {
        try
        {
            return BsonSerializer.Deserialize<ReAccreditationPayload>(
                workItem.Payload ?? new BsonDocument()
            );
        }
        catch (Exception ex)
            when (ex is BsonSerializationException or FormatException or InvalidCastException)
        {
            logger?.LogError(
                ex,
                "Re-accreditation determination for work item {WorkItemId} refused: "
                    + "existing payload could not be deserialised. Writing it would destroy "
                    + "existing payload data (organisation name, registration number, nation, etc.).",
                workItem.Id
            );
            return null;
        }
    }

    /// <summary>
    /// Applies <paramref name="update"/> to the work item's payload and
    /// returns the updated model, or null (logged) when the stored payload
    /// cannot be read — writing over it would destroy existing payload data
    /// (organisation name, registration number, nation, etc.).
    /// </summary>
    public static ReAccreditationPayload? TryUpdatePayload(
        WorkItem workItem,
        Func<ReAccreditationPayload, ReAccreditationPayload> update,
        ILogger logger
    )
    {
        // Deserialise → with-mutate → reserialise so the mongo-driver's
        // serializer (camelCase convention, nested record) is the single
        // source of truth for the on-disk shape.
        if (TryReadPayload(workItem, logger) is not { } payload)
        {
            return null;
        }

        var updated = update(payload);

        // RA-249: merge the modelled updates back over the *existing* payload rather
        // than replacing it wholesale. A full replace against this
        // [BsonIgnoreExtraElements] model dropped every unmodelled payload key
        // (applicationReference, source, siteAddress*, ...) on approval, which turned
        // the application ref into the work-item Guid downstream.
        // Note: Merge is a shallow top-level merge — an unmodelled key nested
        // *inside* a modelled top-level object would still be lost (that object is
        // round-tripped through the model before overwriting). All keys at risk today
        // (applicationReference, source, siteAddress*) are top-level, so they survive.
        var merged = (workItem.Payload ?? new BsonDocument()).DeepClone().AsBsonDocument;
        merged.Merge(updated.ToBsonDocument(), overwriteExistingElements: true);
        workItem.ReplacePayload(merged);
        return updated;
    }

    /// <summary>
    /// Runs every post-action hook for a committed write. A failing hook is
    /// logged, never rethrown: the write has landed.
    /// </summary>
    public static async Task InvokeActionAppliedHooksAsync(
        IReadOnlyList<IWorkItemPostActionHook> hooks,
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        ILogger logger
    )
    {
        // The transition is already committed by the time hooks run, so a client
        // that gives up waiting (management-fe times out) must not cancel them:
        // the operator-backend push and its audit entries would be lost, leaving
        // this service and the operator disagreeing about the application's state.
        foreach (var hook in hooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(
                    workItem,
                    actionId,
                    fromStateId,
                    user,
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                logger.LogError(
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
    /// A 401-shaped failure when <paramref name="user"/> carries no
    /// <c>user:id</c> claim, otherwise null.
    /// </summary>
    public static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user) =>
        string.IsNullOrWhiteSpace(user.FindFirstValue("user:id"))
            ? WorkItemActionResult.Failure(
                WorkItemActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; "
                    + "the request did not include a 'user:id' claim."
            )
            : null;
}
