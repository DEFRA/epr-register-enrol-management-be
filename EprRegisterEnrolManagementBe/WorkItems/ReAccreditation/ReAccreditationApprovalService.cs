using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using EprRegisterEnrolManagementBe.Config;
using EprRegisterEnrolManagementBe.Integrations.OperatorBackend;
using EprRegisterEnrolManagementBe.Utils.Background;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using ReAccreditationSlaClock = EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models.SlaClock;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-132 default <see cref="IReAccreditationApprovalService"/>.
///
/// On a single <see cref="IWorkItemPersistence.ReplaceAsync"/> call the
/// service:
/// <list type="bullet">
///   <item>Validates the work item exists, is a re-accreditation, is
///   readable by the caller, and is in <c>assessment-in-progress</c>.
///   RA-323: any authenticated caseworker may approve — there is no
///   separate decision-maker role.</item>
///   <item>Stamps a fresh accreditation id, today's date as the
///   <c>AccreditationStartDate</c>, and a non-null
///   <see cref="SlaClock.StoppedAt"/> on the payload.</item>
///   <item>Transitions <c>StateId</c> to <c>approved</c>.</item>
///   <item>Appends three audit entries in order: <c>approved</c> (the
///   action-applied entry), <c>sla-clock-stopped</c>, and
///   <c>accreditation-issued</c>.</item>
/// </list>
///
/// On a <see cref="WorkItemConcurrencyException"/> the operation is
/// retried by reloading the latest document and re-running validation,
/// up to <see cref="MaxAttempts"/> times — mirroring the
/// <c>WorkItemAuditAppender</c> retry pattern.
///
/// After success the service enqueues a background job that appends a
/// <c>publishing-enqueued</c> audit entry via a scoped
/// <see cref="IWorkItemAuditAppender"/>, then invokes the registered
/// <see cref="IWorkItemPostActionHook"/>s with action id
/// <c>approve</c> so the notification hook fires the decision email.
///
/// RA-448 phase 2: the accreditation id is no longer fabricated locally —
/// it is resolved from the real backend via <see cref="IAccreditationNumberAdapter"/>,
/// which returns a genuine, DEFRA-public-register-format number rather than
/// the retired <c>AccreditationIdGenerator</c>'s local shape.
/// </summary>
internal sealed class ReAccreditationApprovalService(
    IWorkItemPersistence persistence,
    IWorkItemRegistry registry,
    IAccreditationNumberAdapter accreditationNumberAdapter,
    IBackgroundTaskQueue backgroundTaskQueue,
    IEnumerable<IWorkItemPostActionHook> postActionHooks,
    ILogger<ReAccreditationApprovalService> logger,
    IOptions<AccreditationConfig> accreditationOptions,
    TimeProvider? timeProvider = null
) : IReAccreditationApprovalService
{
    private const int MaxAttempts = 3;
    private const string FromStateId = "awaiting-decision";
    private const string ToStateId = "approved";
    private const string ActionId = "approve";

    // RA-448 phase 2: mirrors the backend's own AC1 format regex
    // (accreditation-number-generation.md), anchored to the 'A' prefix since
    // this module only ever deals with accreditation numbers.
    private static readonly Regex s_expectedAccreditationNumberFormat = new(
        @"^A\d{2}(ER|EX|NR|NX|SR|SX|WR|WX)\d{6}\d{4}(AL|FB|GO|GR|PA|PL|ST|WO)$",
        RegexOptions.Compiled
    );

    // RA-448 phase 2: fixed width of the retired local AccreditationIdGenerator's
    // format (A{Year:2}{Agency:1}{OperatorType:1}{OrgId:6}{PostcodeSuffix:3}{Material:2}
    // = 1+2+1+1+6+3+2 = 16). Used as the "is this the one known, explainable
    // legacy shape" signal below — anything else non-matching stays treated
    // as genuinely unexplained/corrupt, same as before this phase.
    private const int LegacyAccreditationIdLength = 16;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IWorkItemPostActionHook[] _postActionHooks = postActionHooks.ToArray();
    private readonly AccreditationConfig _accreditationConfig = accreditationOptions.Value;

    public async Task<WorkItemActionResult> ApproveAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(user);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        WorkItem? approved = null;
        // RA-448 phase 2: resolved at most once per ApproveAsync call and
        // reused across concurrency retries below. The adapter call is a
        // real, effectful backend request — unlike the old local generator,
        // calling it again on every WorkItemConcurrencyException retry would
        // ask the backend to "reapply" (YY+1) a number it just issued a
        // moment ago on attempt 1, corrupting the count for no reason. See
        // Phase 2 doc AC10/AC11.
        AccreditationNumberResult? numberResult = null;
        // RA-448 phase 2: one id for the whole logical approval attempt (not
        // per retry), forwarded to the backend as X-Correlation-Id so this
        // service's logs and the backend's logs for the same request can be
        // joined on one value.
        var correlationId = Guid.NewGuid();
        var accreditationYear = _accreditationConfig.CurrentYear;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var workItem = await persistence.GetByIdAsync(workItemId, cancellationToken);

            if (workItem is null)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.WorkItemNotFound,
                    $"No work item exists with id '{workItemId}'."
                );
            }

            if (
                !string.Equals(
                    workItem.TypeId,
                    ReAccreditationType.Id,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Work item {workItemId} is of type '{workItem.TypeId}', not '{ReAccreditationType.Id}'."
                );
            }

            // RA-133: idempotent re-approve. If the payload already
            // carries an accreditation id and the item is in the
            // approved state, return success without re-stamping or
            // re-emitting audit/notification side-effects — regardless of
            // that id's format: RA-448 phase 2 deliberately does not
            // retroactively fix already-approved records (Phase 2 doc AC12),
            // so this branch is unchanged from before.
            //
            // A present id on a NOT-yet-approved item is more nuanced. Only a
            // value matching the RETIRED local AccreditationIdGenerator's
            // known, specific shape (fixed-width, exactly
            // LegacyAccreditationIdLength characters) is treated as unset and
            // falls through to (re)issue a real one below — that is the one
            // explainable "known legacy data" case RA-448 phase 2 exists to
            // migrate. Anything else non-matching — the real backend format
            // (still unexplained/corrupt pre-approval; a genuine number is
            // only ever stamped by this same flow, on an item that then
            // transitions to 'approved'), or any other unrecognised shape —
            // is still treated as corrupt, exactly as before this change:
            // silently reissuing a number over genuinely unexplained data
            // would mask whatever produced it.
            var existingAccreditationId = TryReadString(workItem.Payload, "accreditationId");
            if (!string.IsNullOrWhiteSpace(existingAccreditationId))
            {
                if (string.Equals(workItem.StateId, ToStateId, StringComparison.OrdinalIgnoreCase))
                {
                    return WorkItemActionResult.Success(workItem);
                }

                var isKnownLegacyFormat =
                    existingAccreditationId.Length == LegacyAccreditationIdLength
                    && !s_expectedAccreditationNumberFormat.IsMatch(existingAccreditationId);
                if (!isKnownLegacyFormat)
                {
                    logger.LogWarning(
                        "Work item {WorkItemId} carries accreditation id {AccreditationId} but is in state "
                            + "{StateId}; refusing to re-issue.",
                        workItem.Id,
                        existingAccreditationId,
                        workItem.StateId
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.InvalidTransition,
                        $"Work item '{workItemId}' already carries accreditation id "
                            + $"'{existingAccreditationId}' but is in state '{workItem.StateId}'. "
                            + "Manual investigation required."
                    );
                }

                logger.LogInformation(
                    "Work item {WorkItemId} carries a non-standard-format accreditation id "
                        + "{AccreditationId}; treating it as unset and issuing a real one via the backend.",
                    workItem.Id,
                    existingAccreditationId
                );
            }

            if (
                string.Equals(workItem.StateId, ToStateId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(workItem.StateId, "rejected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(workItem.StateId, "withdrawn", StringComparison.OrdinalIgnoreCase)
            )
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.TerminalState,
                    $"Work item {workItemId} is in terminal state '{workItem.StateId}'; no actions are allowed."
                );
            }

            if (!string.Equals(workItem.StateId, FromStateId, StringComparison.OrdinalIgnoreCase))
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Action '{ActionId}' moves work items from '{FromStateId}', "
                        + $"but {workItemId} is in '{workItem.StateId}'."
                );
            }

            // Resolved before any side effect: an approval on a work item
            // whose type is neither registered nor snapshotted issues no
            // accreditation id, stops no SLA clock, queues no publishing job,
            // changes no state and writes no audit entry.
            var template = WorkItemEngineRules.ResolveTemplate(workItem, registry);
            if (template is null)
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Work item {workItemId} references unregistered type '{workItem.TypeId}' "
                        + "and has no stored template snapshot."
                );
            }

            var now = _timeProvider.GetUtcNow();
            var nowUtc = now.UtcDateTime;
            // Scoped to THIS iteration only (unlike numberResult): a retry
            // iteration re-fetches a fresh workItem, so a payload deserialised
            // on an earlier iteration must never be reused for the merge
            // below — only the value set in the SAME iteration it is passed
            // into TryApplyApprovalToPayload is safe to reuse.
            ReAccreditationPayload? numberPayload = null;

            if (numberResult is null)
            {
                ReAccreditationPayload freshPayload;
                try
                {
                    freshPayload = BsonSerializer.Deserialize<ReAccreditationPayload>(
                        workItem.Payload ?? new BsonDocument()
                    );
                }
                catch (Exception ex)
                    when (ex
                            is BsonSerializationException
                                or FormatException
                                or InvalidCastException
                    )
                {
                    logger.LogError(
                        ex,
                        "Re-accreditation approval for work item {WorkItemId} aborted: existing payload "
                            + "could not be deserialised to resolve the fields needed to request an "
                            + "accreditation number.",
                        workItem.Id
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.InvalidTransition,
                        $"Work item '{workItemId}' payload is corrupt and cannot be read. "
                            + "Inspect the server logs for details; a manual data repair may be required."
                    );
                }

                if (
                    freshPayload.Nation is not { } nation
                    || string.IsNullOrWhiteSpace(freshPayload.OperatorOrganisationId)
                    || string.IsNullOrWhiteSpace(freshPayload.OperatorRegistrationId)
                    || !int.TryParse(
                        freshPayload.OperatorOrganisationId,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var orgId
                    )
                )
                {
                    logger.LogError(
                        "Work item {WorkItemId} is missing data required to request an accreditation number "
                            + "(nation, a numeric operatorOrganisationId, or operatorRegistrationId).",
                        workItem.Id
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.InvalidTransition,
                        $"Work item '{workItemId}' is missing data required to issue an accreditation number."
                    );
                }

                // RA-448 phase 2: OperatorRegistrationId is used as the backend's
                // {applicationId} route segment — a working assumption, not a
                // verified fact (Phase 2 doc AC11). Always pass Regenerate=true:
                // the backend itself decides generate-vs-reapply based on
                // whether IT already has a stored number for this application
                // (AccreditationApplicationModel.AccreditationReference), so
                // this single call is correct whether this is the
                // application's first accreditation or an annual reapply.
                numberResult =
                    await accreditationNumberAdapter.GenerateOrUpdateAccreditationNumberAsync(
                        freshPayload.OperatorOrganisationId,
                        freshPayload.OperatorRegistrationId,
                        nation,
                        orgId,
                        accreditationYear,
                        regenerate: true,
                        correlationId,
                        cancellationToken
                    );

                if (!numberResult.IsSuccess)
                {
                    logger.LogError(
                        "Failed to resolve an accreditation number for work item {WorkItemId} "
                            + "(correlation {CorrelationId}): {Error}",
                        workItem.Id,
                        correlationId,
                        numberResult.ErrorMessage
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.AccreditationNumberUnavailable,
                        $"Work item '{workItemId}' could not be approved: an accreditation number could "
                            + "not be obtained. Try again, or contact support if the problem persists."
                    );
                }

                // Reused by TryApplyApprovalToPayload below so the same,
                // not-yet-mutated payload is not deserialised a second time
                // on the common path (numberResult resolved on attempt 1).
                numberPayload = freshPayload;
            }

            var accreditationId = numberResult.AccreditationNumber!;

            // RA-133: start date is 1 Jan of the configured year, OR
            // today's date when approval happens after 1 Jan of that
            // year (the accreditation cannot start in the past).
            var jan1 = new DateOnly(accreditationYear, 1, 1);
            var today = DateOnly.FromDateTime(nowUtc);
            var accreditationStartDate = today > jan1 ? today : jan1;

            if (
                !TryApplyApprovalToPayload(
                    workItem,
                    accreditationId,
                    accreditationStartDate,
                    accreditationYear,
                    now,
                    numberPayload
                )
            )
            {
                return WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.InvalidTransition,
                    $"Work item '{workItemId}' payload is corrupt and cannot be read. "
                        + "Inspect the server logs for details; a manual data repair may be required."
                );
            }

            var previousState = workItem.StateId;
            workItem.StateId = ToStateId;
            workItem.LastModifiedAt = nowUtc;

            var auditDetails = new Dictionary<string, string?>
            {
                ["actionId"] = ActionId,
                ["actionDisplayName"] = "Approve",
                ["fromStateId"] = previousState,
                ["toStateId"] = workItem.StateId,
            };
            AppendAudit(workItem, "action-applied", "Action applied", user, nowUtc, auditDetails);
            AppendAudit(
                workItem,
                "sla-clock-stopped",
                "SLA clock stopped",
                user,
                nowUtc,
                new() { ["stoppedAt"] = now.ToString("O") }
            );
            AppendAudit(
                workItem,
                "accreditation-issued",
                "Accreditation issued",
                user,
                nowUtc,
                new()
                {
                    ["accreditationId"] = accreditationId,
                    ["startDate"] = accreditationStartDate.ToString("yyyy-MM-dd"),
                    ["accreditationYear"] = accreditationYear.ToString(
                        CultureInfo.InvariantCulture
                    ),
                }
            );

            try
            {
                await persistence.ReplaceAsync(workItem, cancellationToken);
                approved = workItem;
                break;
            }
            catch (WorkItemConcurrencyException)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(
                        "ReAccreditation approval for work item {WorkItemId} abandoned after {Attempts} attempts "
                            + "due to repeated concurrency conflicts.",
                        workItemId,
                        MaxAttempts
                    );
                    return WorkItemActionResult.Failure(
                        WorkItemActionFailureCode.ConcurrencyConflict,
                        $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
                    );
                }
            }
        }

        // Loop has either returned (validation failure / exhausted retries)
        // or assigned `approved`. The compiler does not see the latter so
        // narrow with an assertion.
        var persisted = approved!;

        logger.LogInformation(
            "Re-accreditation work item {WorkItemId} approved by {UserId}",
            persisted.Id,
            user.FindFirstValue("user:id")
        );

        await EnqueuePublishingAuditAsync(persisted, user, cancellationToken);
        await InvokeActionAppliedHooksAsync(
            persisted,
            ActionId,
            FromStateId,
            user,
            cancellationToken
        );

        return WorkItemActionResult.Success(persisted);
    }

    private bool TryApplyApprovalToPayload(
        WorkItem workItem,
        string accreditationId,
        DateOnly accreditationStartDate,
        int accreditationYear,
        DateTimeOffset stoppedAt,
        ReAccreditationPayload? preDeserializedPayload
    )
    {
        ReAccreditationPayload payload;
        if (preDeserializedPayload is not null)
        {
            // Caller already deserialised the same, not-yet-mutated
            // workItem.Payload this iteration (to resolve the number-request
            // fields) — reuse it rather than parsing the identical document
            // a second time.
            payload = preDeserializedPayload;
        }
        else
        {
            // Deserialise → with-mutate → reserialise so the mongo-driver's
            // serializer (camelCase convention, nested record) is the single
            // source of truth for the on-disk shape.
            try
            {
                payload = BsonSerializer.Deserialize<ReAccreditationPayload>(
                    workItem.Payload ?? new BsonDocument()
                );
            }
            catch (Exception ex)
                when (ex is BsonSerializationException or FormatException or InvalidCastException)
            {
                logger.LogError(
                    ex,
                    "Re-accreditation approval for work item {WorkItemId} aborted: "
                        + "existing payload could not be deserialised. Approving would destroy "
                        + "existing payload data (organisation name, registration number, nation, etc.).",
                    workItem.Id
                );
                return false;
            }
        }

        var updated = payload with
        {
            AccreditationId = accreditationId,
            AccreditationStartDate = accreditationStartDate,
            AccreditationYear = accreditationYear,
            SlaClock = new ReAccreditationSlaClock(stoppedAt),
        };

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
        return true;
    }

    private async Task EnqueuePublishingAuditAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        // ClaimsPrincipal holds only in-memory state, so it is safe to
        // close over and hand to the background job; the queued
        // delegate runs on its own DI scope.
        var workItemId = workItem.Id;
        var accreditationIdValue = TryReadString(workItem.Payload, "accreditationId");

        await backgroundTaskQueue.QueueAsync(
            async (scopedServices, ct) =>
            {
                var appender = scopedServices.GetRequiredService<IWorkItemAuditAppender>();
                await appender.AppendAsync(
                    workItemId,
                    action: "publishing-enqueued",
                    actionDisplayName: "Publishing enqueued",
                    details: new Dictionary<string, string?>
                    {
                        ["accreditationId"] = accreditationIdValue,
                    },
                    user,
                    ct
                );
            },
            cancellationToken
        );
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

    private static void AppendAudit(
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
            }
        );
    }

    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal user)
    {
        var id = user.FindFirstValue("user:id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.MissingActorIdentity,
            "Mutating this work item requires an authenticated end user; "
                + "the request did not include a 'user:id' claim."
        );
    }

    private static string? TryReadString(BsonDocument? document, string fieldName)
    {
        if (document is null || !document.TryGetValue(fieldName, out var value))
        {
            return null;
        }
        return value.IsBsonNull ? null : value.ToString();
    }
}
