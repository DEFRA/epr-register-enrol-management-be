using System.Globalization;
using System.Security.Claims;
using System.Xml;

namespace EprRegisterEnrolManagementBe.WorkItems.Core;

/// <summary>
/// RA-131: framework service that handles SLA extension and manual
/// override for any work item type. Lives in core because the rules
/// (non-empty reason, audit composition, operator-notify-on-extend) are
/// universal across modules. RA-323: any authenticated caseworker may
/// extend or override — there is no team-leader gate any more. RA-447/CM6:
/// extend has no upper limit any more (SlaConfig.MaxExtensionDays removed).
/// RA-601: extend has no lower bound either — a negative
/// <c>additionalDuration</c> moves the determination deadline EARLIER. Only
/// zero is rejected, as a no-op.
/// </summary>
public interface ISlaService
{
    /// <summary>
    /// Add <paramref name="additionalDuration"/> to the work item's
    /// <see cref="SlaClock.TargetDuration"/>. RA-601: the duration may be
    /// negative (ISO-8601 <c>-P14D</c>) to move the deadline earlier; zero is
    /// rejected. Writes an <c>sla-extended</c>
    /// audit entry carrying before/after SlaClock snapshots and the
    /// supplied reason, and fans out to every registered
    /// <see cref="IWorkItemPostActionHook"/> with an <c>sla-extend</c>
    /// action id so per-module notification hooks (e.g. the operator
    /// "Determination deadline extended" email) fire automatically.
    /// </summary>
    Task<SlaActionResult> ExtendAsync(
        Guid workItemId,
        TimeSpan additionalDuration,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the work item's <see cref="SlaClock"/> with the supplied
    /// fields. <paramref name="newStartedAt"/> is optional — when omitted
    /// the existing start is preserved so an override that just
    /// re-targets the deadline keeps the historical start. Writes an
    /// <c>sla-overridden</c> audit entry carrying before/after snapshots
    /// and the reason; deliberately does NOT trigger the notification
    /// hook because overrides are regulator-internal.
    /// </summary>
    Task<SlaActionResult> OverrideAsync(
        Guid workItemId,
        TimeSpan newTargetDuration,
        DateTime? newStartedAt,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Failure reasons returned by <see cref="ISlaService"/>. Distinct from
/// <see cref="WorkItemActionFailureCode"/> so SLA-only outcomes (clock
/// missing, validation specific to SLA shape) can be expressed without
/// muddying the engine's failure taxonomy.
/// </summary>
public enum SlaActionFailureCode
{
    WorkItemNotFound,
    NotAuthorized,
    /// <summary>Caller carries no <c>user:id</c> claim (BFF did not forward identity).</summary>
    MissingActorIdentity,
    /// <summary>Reason / duration / start-date failed structural validation. Maps to 422.</summary>
    InvalidRequest,
    /// <summary>SLA clock has not been started for this work item. Maps to 409.</summary>
    ClockNotStarted,
    /// <summary>Optimistic-concurrency collision. Maps to 409.</summary>
    ConcurrencyConflict
}

/// <summary>Outcome of an <see cref="ISlaService"/> mutation.</summary>
public sealed record SlaActionResult(
    WorkItem? WorkItem,
    SlaActionFailureCode? FailureCode,
    string? Message)
{
    public bool IsSuccess => FailureCode is null;

    public static SlaActionResult Success(WorkItem workItem) =>
        new(workItem, null, null);

    public static SlaActionResult Failure(SlaActionFailureCode code, string message) =>
        new(null, code, message);
}

public sealed class SlaService : ISlaService
{
    /// <summary>
    /// Action id used when fanning out post-action hooks after a
    /// successful extend. The existing per-module notification hooks
    /// (e.g. <c>ReAccreditationNotificationHook</c>) map this id to the
    /// <c>SlaExtended</c> Notify template.
    /// </summary>
    public const string ExtendActionId = "sla-extend";

    private readonly IWorkItemPersistence _persistence;
    private readonly ILogger<SlaService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyCollection<IWorkItemPostActionHook> _postActionHooks;

    public SlaService(
        IWorkItemPersistence persistence,
        ILogger<SlaService> logger,
        TimeProvider? timeProvider = null,
        IEnumerable<IWorkItemPostActionHook>? postActionHooks = null)
    {
        _persistence = persistence;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _postActionHooks = postActionHooks?.ToArray() ?? Array.Empty<IWorkItemPostActionHook>();
    }

    public async Task<SlaActionResult> ExtendAsync(
        Guid workItemId,
        TimeSpan additionalDuration,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure) return identityFailure;
        if (RequireReason(reason) is { } reasonFailure) return reasonFailure;

        if (additionalDuration == TimeSpan.Zero)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                "'additionalDuration' must be a non-zero ISO-8601 duration — " +
                "'P14D' to move the determination deadline later, '-P14D' to move it earlier.");
        }

        // RA-601: a NEGATIVE additionalDuration is valid — it moves the
        // determination deadline earlier. The old "must be positive" guard was
        // never a requirement. RA-447/CM6 only asked for an absolute date
        // picker with no upper limit on an extension; the extension-only rule
        // was an artefact of the day-count field RA-447 replaced, which simply
        // could not express a backwards move. RA-572 then renamed the action
        // from "Extend" to "Change", which made the restriction visibly wrong,
        // and RA-601 removes it.
        //
        // The product owner explicitly chose NO floor. Consequences, accepted:
        //
        //  * A deadline earlier than today is valid. Mind the two senses of
        //    "breached" here, because in the short term they disagree. The
        //    COMPUTED state (see ComputeState on WorkItemSlaClock, surfaced as
        //    slaState on the wire) reports Breached on the very next read,
        //    since Remaining is already negative. The PERSISTED boolean on the
        //    clock stays false until the nightly SlaBreachBackgroundService
        //    sweep flips it, so a backdated item really does read as
        //    not-breached in Mongo in the meantime. Verified against a live
        //    stack during RA-601, so do not "simplify" the two into one.
        //
        //  * That sweep is one-way. It skips items already flagged, and nothing
        //    anywhere clears the flag, so once it catches an overdue item both
        //    the flag and its sla-breached audit entry are permanent even if
        //    the deadline is later moved back into the future. RA-601 did NOT
        //    introduce that: an item whose deadline simply lapses has always
        //    reached the same permanently-breached state. What changes here is
        //    only that reaching it becomes deliberate and immediate rather than
        //    a matter of waiting. The irreversibility is pre-existing
        //    management-be behaviour, is being escalated on its own merits, and
        //    is deliberately NOT addressed here — it is not a reason to put a
        //    floor back on this method.
        //
        //  * A deadline earlier than the clock's StartedAt is valid too, which
        //    drives TargetDuration negative or zero. Every read path tolerates
        //    that: see the DueAt and Remaining members of WorkItemSlaClock,
        //    which saturate rather than overflow.
        //
        // Only zero is rejected, because re-submitting the current deadline is
        // a no-op, and the frontend rejects it for the same reason. There is no
        // upper limit either, since RA-447/CM6 removed the old MaxExtensionDays
        // cap.

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }
        if (workItem.SlaClock is null)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.ClockNotStarted,
                $"Work item '{workItemId}' has no SLA clock started — extend / override is unavailable.");
        }

        // RA-601: this is NOT the floor the product owner declined — it is the
        // edge of what a .NET DateTime/TimeSpan can represent at all. Without
        // it a caller could send '-P4000000D' and either overflow the TimeSpan
        // addition below (unhandled exception => 500) or persist a clock whose
        // deadline sits outside DateTime's range, which every subsequent read
        // of that work item would have to cope with. A date that cannot exist
        // is not "an earlier real date", so rejecting it takes nothing away
        // from the RA-601 decision.
        if (!TryShiftTarget(workItem.SlaClock, additionalDuration, out var newTargetDuration))
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                "'additionalDuration' would move the determination deadline outside " +
                "the range of representable dates.");
        }

        var before = Snapshot(workItem.SlaClock);
        workItem.SlaClock.TargetDuration = newTargetDuration;
        var after = Snapshot(workItem.SlaClock);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.LastModifiedAt = now;

        AppendAuditEntry(
            workItem,
            action: "sla-extended",
            actionDisplayName: "Determination deadline extended",
            user,
            now,
            reason,
            before,
            after,
            extra: new Dictionary<string, string?>
            {
                ["additionalDuration"] = XmlConvert.ToString(additionalDuration)
            });

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.ConcurrencyConflict,
                $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry.");
        }

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) SLA extended by {AdditionalDuration} (target now {TargetDuration}) by {User}",
            workItem.Id, workItem.TypeId, additionalDuration, workItem.SlaClock.TargetDuration, DescribeUser(user));

        await InvokeExtendHooksAsync(workItem, user, cancellationToken);

        return SlaActionResult.Success(workItem);
    }

    public async Task<SlaActionResult> OverrideAsync(
        Guid workItemId,
        TimeSpan newTargetDuration,
        DateTime? newStartedAt,
        string reason,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (RequireActorIdentity(user) is { } identityFailure) return identityFailure;
        if (RequireReason(reason) is { } reasonFailure) return reasonFailure;

        if (newTargetDuration <= TimeSpan.Zero)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                "'newTargetDuration' must be a positive ISO-8601 duration (e.g. 'P21D').");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (newStartedAt is { } startedAt)
        {
            // Normalise to UTC so a caller-supplied offset cannot smuggle
            // a future wallclock past the guard.
            var startedAtUtc = startedAt.Kind == DateTimeKind.Utc
                ? startedAt
                : startedAt.ToUniversalTime();
            if (startedAtUtc > now)
            {
                return SlaActionResult.Failure(
                    SlaActionFailureCode.InvalidRequest,
                    "'newStartedAt' must not be in the future.");
            }
            newStartedAt = startedAtUtc;
        }
        else
        {
            // BA confirmed (RA-131): omitting newStartedAt should default the
            // clock start to today rather than preserving the existing value.
            newStartedAt = now;
        }

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'.");
        }
        if (workItem.SlaClock is null)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.ClockNotStarted,
                $"Work item '{workItemId}' has no SLA clock started — extend / override is unavailable.");
        }

        // No cap on override — regulators agree duration offline with operators
        // (BA confirmed RA-131; no legislative mandate for a maximum).

        var before = Snapshot(workItem.SlaClock);
        workItem.SlaClock.TargetDuration = newTargetDuration;
        workItem.SlaClock.StartedAt = newStartedAt.Value;
        var after = Snapshot(workItem.SlaClock);
        workItem.LastModifiedAt = now;

        AppendAuditEntry(
            workItem,
            action: "sla-overridden",
            actionDisplayName: "SLA overridden",
            user,
            now,
            reason,
            before,
            after,
            extra: null);

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.ConcurrencyConflict,
                $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry.");
        }

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) SLA overridden (target now {TargetDuration}, started {StartedAt}) by {User}",
            workItem.Id, workItem.TypeId, workItem.SlaClock.TargetDuration, workItem.SlaClock.StartedAt, DescribeUser(user));

        return SlaActionResult.Success(workItem);
    }

    /// <summary>
    /// RA-601: compute <c>clock.TargetDuration + additionalDuration</c> without
    /// ever throwing, and report whether the result still yields a deadline
    /// (<c>StartedAt + TargetDuration</c>) inside the representable DateTime
    /// range. Returns <c>false</c> instead of throwing on TimeSpan overflow.
    /// </summary>
    private static bool TryShiftTarget(
        WorkItemSlaClock clock, TimeSpan additionalDuration, out TimeSpan newTargetDuration)
    {
        newTargetDuration = TimeSpan.Zero;

        // TimeSpan operator+ throws on overflow; the tick arithmetic below
        // cannot, so do the range check on ticks first.
        var currentTicks = clock.TargetDuration.Ticks;
        var deltaTicks = additionalDuration.Ticks;
        var sum = unchecked(currentTicks + deltaTicks);
        if (((currentTicks ^ sum) & (deltaTicks ^ sum)) < 0)
        {
            return false; // long overflow => TimeSpan overflow.
        }

        var startedAtTicks = clock.StartedAt.Ticks;
        var deadlineTicks = unchecked(startedAtTicks + sum);
        if (((startedAtTicks ^ deadlineTicks) & (sum ^ deadlineTicks)) < 0 ||
            deadlineTicks < DateTime.MinValue.Ticks ||
            deadlineTicks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        newTargetDuration = TimeSpan.FromTicks(sum);
        return true;
    }

    private static Dictionary<string, string?> Snapshot(WorkItemSlaClock clock) => new()
    {
        ["startedAt"] = clock.StartedAt.ToString("o", CultureInfo.InvariantCulture),
        ["targetDuration"] = XmlConvert.ToString(clock.TargetDuration),
        ["breached"] = clock.Breached.ToString()
    };

    private static void AppendAuditEntry(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal user,
        DateTime createdAt,
        string reason,
        Dictionary<string, string?> before,
        Dictionary<string, string?> after,
        Dictionary<string, string?>? extra)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["actorUserId"] = ResolveActorUserId(user),
            ["beforeStartedAt"] = before["startedAt"],
            ["beforeTargetDuration"] = before["targetDuration"],
            ["beforeBreached"] = before["breached"],
            ["afterStartedAt"] = after["startedAt"],
            ["afterTargetDuration"] = after["targetDuration"],
            ["afterBreached"] = after["breached"]
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra) details[k] = v;
        }

        workItem.AuditLog.Add(new WorkItemAuditEntry
        {
            Action = action,
            ActionDisplayName = actionDisplayName,
            Details = details,
            CreatedAt = createdAt,
            CreatedBy = ResolveActorUserId(user),
            CreatedByName = user.FindFirstValue("user:name"),
            // epr-rr9s: snapshot the state as of this event. SLA actions do
            // not mutate StateId, so this is the state the item was in when
            // the deadline was extended or overridden.
            StateId = workItem.StateId
        });
    }

    private async Task InvokeExtendHooksAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(
                    workItem, ExtendActionId, workItem.StateId, user, cancellationToken);
            }
            catch (Exception ex)
            {
                // Hook contract requires side-effect failures be swallowed
                // and self-recorded; we still defensively catch so a
                // misbehaving hook cannot unwind the persisted extend.
                _logger.LogError(ex,
                    "SLA-extend post-action hook {HookType} failed for work item {WorkItemId}",
                    hook.GetType().FullName, workItem.Id);
            }
        }
    }

    private static SlaActionResult? RequireActorIdentity(ClaimsPrincipal? user) =>
        ResolveActorUserId(user) is null
            ? SlaActionResult.Failure(
                SlaActionFailureCode.MissingActorIdentity,
                "Mutating this work item requires an authenticated end user; " +
                "the request did not include a 'user:id' claim.")
            : null;

    private static SlaActionResult? RequireReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                "'reason' is required and must not be whitespace.")
            : null;

    private static string? ResolveActorUserId(ClaimsPrincipal? user)
    {
        var id = user?.FindFirstValue("user:id");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static string DescribeUser(ClaimsPrincipal? user) =>
        user?.FindFirstValue("user:id")
        ?? user?.FindFirstValue("client_id")
        ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
