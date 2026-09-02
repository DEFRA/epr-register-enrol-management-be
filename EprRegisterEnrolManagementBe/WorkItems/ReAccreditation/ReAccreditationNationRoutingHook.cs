using System.Security.Claims;
using EprRegisterEnrolManagementBe.WorkItems.Core;
using EprRegisterEnrolManagementBe.WorkItems.ReAccreditation.Models;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolManagementBe.WorkItems.ReAccreditation;

/// <summary>
/// RA-526: post-submission hook that reads the UK nation the caller already
/// submitted in the re-accreditation payload's <c>nation</c> field, defaults
/// to England when it's absent or unrecognised, and records a
/// <c>routed-to-nation</c> audit entry either way.
///
/// RA-125 originally derived this from <c>siteAddressPostcode</c> via
/// <see cref="INationResolver"/> - removed here because it was both
/// unreliable (postcode prefixes that straddle a nation border) and, for
/// every real submission, dead code: the caller sends <c>siteAddress</c> as
/// a flat string, not the nested document this hook's old postcode
/// extraction expected, so it always silently resolved England regardless
/// of the real nation. epr-register-enrol-backend now derives Nation
/// reliably from the registration's own regulator and sends it directly, so
/// this hook can just trust that value instead of re-deriving it.
/// <see cref="INationResolver"/>/<see cref="NationResolver"/> are unchanged
/// and still used by <see cref="ReAccreditationSeeder"/> for local dev
/// fixture data, unrelated to this real-submission path.
///
/// All of this happens in a single <see cref="IWorkItemPersistence.ReplaceAsync"/>
/// so the payload update and the audit entry land atomically. Failures are
/// logged and swallowed so a transient DB hiccup does not unwind the
/// originating submission.
///
/// BSON keys are camelCase to match the global
/// <c>CamelCaseElementNameConvention</c> registered in
/// <c>MongoConversions</c> and the casing produced by client JSON
/// submissions (which are stored verbatim by
/// <c>WorkItemPersistence.ToBson</c>).
/// </summary>
internal sealed class ReAccreditationNationRoutingHook(
    IWorkItemPersistence persistence,
    ILogger<ReAccreditationNationRoutingHook> logger,
    TimeProvider? timeProvider = null,
    TimeSpan? retryDelay = null
) : IWorkItemPostActionHook
{
    /// <summary>BSON key (camelCase) under which the caller-submitted / routed nation is read and written.</summary>
    internal const string NationKey = "nation";

    private const string DerivedFromSubmitted = "submitted";
    private const string DerivedFromDefaultEngland = "default-england";

    private const int MaxAttempts = 3;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(50);

    public Task OnSubmittedAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        if (!IsReAccreditation(workItem))
        {
            return Task.CompletedTask;
        }

        return RouteAndRecordAsync(workItem, user, cancellationToken);
    }

    public Task OnActionAppliedAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    ) => Task.CompletedTask;

    private async Task RouteAndRecordAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        var (nation, derivedFrom) = ResolveNation(workItem);
        var nationString = nation.ToString();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var item = await persistence.GetByIdAsync(workItem.Id, cancellationToken);
            if (item is null)
            {
                logger.LogWarning(
                    "Nation routing skipped: work item {WorkItemId} not found.",
                    workItem.Id
                );
                return;
            }

            // Stamp Nation into the payload BSON document.
            item.Payload[NationKey] = nationString;

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            item.AuditLog.Add(
                new WorkItemAuditEntry
                {
                    Action = "routed-to-nation",
                    ActionDisplayName = "Routed to nation",
                    Details = new Dictionary<string, string?>
                    {
                        ["nation"] = nationString,
                        ["derivedFrom"] = derivedFrom,
                    },
                    CreatedAt = now,
                    CreatedBy = user.FindFirstValue("user:id"),
                    CreatedByName = user.FindFirstValue("user:name"),
                    // epr-rr9s: snapshot the work item's state at routing time
                    // (the post-submission initial state). Previously this entry
                    // recorded no state, so the history UI had nothing historical
                    // to show for "Routed to nation".
                    StateId = item.StateId,
                }
            );

            try
            {
                await persistence.ReplaceAsync(item, cancellationToken);
                logger.LogInformation(
                    "Work item {WorkItemId} routed to nation {Nation} ({DerivedFrom}).",
                    workItem.Id,
                    nationString,
                    derivedFrom
                );
                return;
            }
            catch (WorkItemConcurrencyException)
            {
                if (attempt == MaxAttempts)
                {
                    logger.LogError(
                        "Nation routing for work item {WorkItemId} abandoned after {Attempts} attempts; "
                            + "item left unrouted (no payload.nation, no routed-to-nation audit entry).",
                        workItem.Id,
                        MaxAttempts
                    );
                    return;
                }

                // Small jittered backoff so a contended item gets a chance to settle
                // before we re-fetch and retry. Jitter avoids lockstep retries when
                // multiple submissions race.
                if (_retryDelay > TimeSpan.Zero)
                {
                    var jitterMs = Random.Shared.Next(0, (int)_retryDelay.TotalMilliseconds + 1);
                    var delay = TimeSpan.FromMilliseconds(
                        _retryDelay.TotalMilliseconds * attempt + jitterMs
                    );
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
    }

    private static bool IsReAccreditation(WorkItem workItem) =>
        string.Equals(workItem.TypeId, ReAccreditationType.Id, StringComparison.OrdinalIgnoreCase);

    // RA-526: the caller (epr-register-enrol-backend) sends nation as a flat top-level string
    // on the submission payload - trust it when it's a recognised Nation value, otherwise
    // default to England (an absent/unrecognised value is treated the same, deliberately not
    // distinguished any further: neither case has a reliable fallback to derive from any more).
    private static (Nation Nation, string DerivedFrom) ResolveNation(WorkItem workItem)
    {
        if (
            workItem.Payload is not null
            && workItem.Payload.TryGetValue(NationKey, out var element)
            && element.IsString
            && Enum.TryParse<Nation>(element.AsString, ignoreCase: true, out var submittedNation)
        )
        {
            return (submittedNation, DerivedFromSubmitted);
        }

        return (Nation.England, DerivedFromDefaultEngland);
    }
}
