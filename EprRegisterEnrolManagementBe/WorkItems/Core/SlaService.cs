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
/// RA-601: a negative <c>additionalDuration</c> may move the determination
/// deadline EARLIER; RA-611 then put a floor under that — the resulting
/// deadline may not fall before the LATER of the duly-made date (the SLA
/// clock's start) and 1 January of the CURRENT calendar year. Zero is
/// still rejected, as a no-op.
/// </summary>
public interface ISlaService
{
    /// <summary>
    /// Add <paramref name="additionalDuration"/> to the work item's
    /// <see cref="SlaClock.TargetDuration"/>. RA-601: the duration may be
    /// negative (ISO-8601 <c>-P14D</c>) to move the deadline earlier; zero is
    /// rejected. RA-611: a duration that would land the deadline (as a
    /// Europe/London calendar date) before the later of the duly-made date and
    /// 1 January of the current calendar year is rejected with
    /// <see cref="SlaActionFailureCode.InvalidRequest"/> — 422 on the wire.
    /// Writes an <c>sla-extended</c>
    /// audit entry carrying before/after SlaClock snapshots and the
    /// supplied reason, and fans out to every registered
    /// <see cref="IWorkItemPostActionHook"/> with an <c>sla-extend</c>
    /// action id. RA-581: no notification hook maps this action any more —
    /// the operator "Determination deadline changed" email was removed —
    /// so this fan-out is currently a no-op for every registered hook.
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
    /// successful extend. RA-581: <c>ReAccreditationNotificationHook</c>
    /// no longer maps this id to a Notify template — the extend-SLA email
    /// was removed.
    /// </summary>
    public const string ExtendActionId = "sla-extend";

    /// <summary>
    /// RA-611: the regulator's calendar. The deadline floor is decided on UK
    /// local dates, not UTC — during BST a UTC comparison is an hour out either
    /// side of midnight, which is exactly where an off-by-one-day rejection
    /// would bite. Falls back to UTC only on a host with no time-zone database
    /// at all (globalization-invariant mode), where every lookup fails and UTC
    /// is the closest approximation of London available.
    /// </summary>
    private static readonly TimeZoneInfo s_ukTimeZone =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var ukZone)
            ? ukZone
            : TimeZoneInfo.Utc;

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

        // RA-611: the determination deadline MAY be moved backwards — including
        // into the past — but it may not land before the LATER of:
        //
        //   (a) the duly-made date: the SLA clock's StartedAt as a UK calendar
        //       date. The clock is started by the duly-making transition with
        //       StartedAt = midnight UTC of the payment date, so it IS the date
        //       on which the regulator first held everything needed to
        //       determine the application; there is no separate dulyMadeAt.
        //   (b) 1 January of the CURRENT calendar year, from the injected
        //       TimeProvider as a UK date.
        //
        // History, because this rule has now flipped four times. The original
        // "must be positive" guard was an artefact of the day-count field
        // RA-447/CM6 replaced, which could not express a backwards move at all.
        // RA-572 renamed the action from "Extend" to "Change", making the
        // restriction visibly wrong, and RA-601 removed it — deliberately with
        // NO floor whatsoever, so a deadline could be backdated to any
        // representable date. RA-611 first replaced that with a floor of
        // "today", to stop caseworkers dropping live applications straight into
        // a breached state (the nightly SlaBreachBackgroundService sweep that
        // flags it is one-way: nothing clears the flag or its sla-breached
        // audit entry even if the deadline is later moved forward again).
        //
        // The spec then changed MID-BRANCH (Anthony Moody, 29-Sep-2026): a
        // determination can legitimately be BACKDATED, as far back as the
        // duly-made date, but never before 1 January of the current year. So the
        // today-floor is REPLACED, not supplemented — keeping it would make this
        // rule unreachable, because for any live case the duly-made date is
        // already in the past. Backdating into a breached state is therefore
        // legal again where the regulator intends it; what is refused is a
        // deadline earlier than the regulator could possibly have determined the
        // application.
        //
        // The first cut of that second bound read 1 January of
        // payload.accreditationYear, and QA (Giri Nattu, 05-Oct-2026) found it
        // unsatisfiable: accreditationYear is the year the ISSUED accreditation
        // takes effect — stamped at approval from Accreditation:CurrentYear and
        // turned into AccreditationStartDate = 1 Jan of that year — so it runs
        // AHEAD of the determination window. A live case in October 2026 carries
        // 2027, which put the floor at 1 January 2027 and refused every date in
        // 2026, including the deadline the application already had. Anthony's
        // wording was "1st Jan of current year" throughout; it is the CALENDAR
        // year, and it was never the accreditation year.
        //
        // Both bounds are compared as Europe/London CALENDAR DATES rather than
        // UTC instants, because the regulator and the UI both work in UK dates
        // and a UTC comparison is an hour wrong either side of midnight during
        // BST. Landing exactly ON the floor is accepted. The check needs the
        // loaded work item (the deadline is relative to its clock), so it sits
        // below rather than up here with the cheap guards.
        //
        // Zero is still rejected above: re-submitting the current deadline is a
        // no-op, and the frontend rejects it for the same reason. There is still
        // no upper limit, since RA-447/CM6 removed MaxExtensionDays.
        //
        // The wire spelling of a backwards move is unchanged: a negative
        // ISO-8601 duration carries its sign AHEAD of the 'P' (-P14D). See
        // SlaEndpoints.TryParseDuration.

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

        // RA-601: this is NOT the RA-611 business floor below — it is the edge
        // of what a .NET DateTime/TimeSpan can represent at all. Without
        // it a caller could send '-P4000000D' and either overflow the TimeSpan
        // addition below (unhandled exception => 500) or persist a clock whose
        // deadline sits outside DateTime's range, which every subsequent read
        // of that work item would have to cope with. A date that cannot exist
        // is not "an earlier real date", so this guard is about representability
        // only, and it deliberately runs BEFORE the RA-611 floor so an
        // unrepresentable request is reported as such rather than as a deadline
        // below the duly-made / 1-January floor.
        if (!TryShiftTarget(workItem.SlaClock, additionalDuration, out var newTargetDuration))
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                "'additionalDuration' would move the determination deadline outside " +
                "the range of representable dates.");
        }

        // RA-611: floor the RESULTING deadline. TryShiftTarget has already proven
        // StartedAt + newTargetDuration is representable, so this addition —
        // which is exactly SlaClock.DueAt + additionalDuration — cannot throw.
        var newDueAt = workItem.SlaClock.StartedAt + newTargetDuration;
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;

        var dulyMadeDate = UkDateOf(workItem.SlaClock.StartedAt);
        // 1 January of the CURRENT CALENDAR year, taken from the injected
        // TimeProvider and converted to a UK date the same way as the anchor
        // above, so both bounds are read off one calendar.
        //
        // Do NOT reach for the Accreditation:CurrentYear setting here, however
        // well its name reads. That key holds the accreditation year currently
        // OPEN for applications (2027 while this is being written), which is the
        // year an ISSUED accreditation takes effect, not the year we are in. It
        // is the field this bound was first built on, and QA found it refused
        // every date in the determination window; see the block above. Nothing in
        // this floor may consult WorkItem.Payload either, for the same reason.
        //
        // The floor is therefore time-dependent again: on New Year's Day it
        // steps forward a year, intentionally — that is what "no further back
        // than 1st Jan of current year" means.
        var currentYearStart = new DateOnly(UkDateOf(utcNow).Year, 1, 1);
        // Whichever bound is LATER is the one that actually constrains the
        // caseworker, and the one the error message must name — telling them
        // "not before 1 January" when the real floor is a March duly-made date
        // would send them round the loop a second time. A tie (the application
        // was duly made on 1 January) names 1 January, which is the same date
        // either way.
        var yearStartBinds = currentYearStart >= dulyMadeDate;
        var floor = yearStartBinds ? currentYearStart : dulyMadeDate;

        if (UkDateOf(newDueAt) < floor)
        {
            return SlaActionResult.Failure(
                SlaActionFailureCode.InvalidRequest,
                yearStartBinds
                    ? $"The new determination deadline cannot be earlier than 1 January {floor.Year}."
                    : "The new determination deadline cannot be earlier than " +
                      $"{floor.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}, " +
                      "when the application was duly made.");
        }

        var before = Snapshot(workItem.SlaClock);
        workItem.SlaClock.TargetDuration = newTargetDuration;
        var after = Snapshot(workItem.SlaClock);
        workItem.LastModifiedAt = utcNow;

        AppendAuditEntry(
            workItem,
            action: "sla-extended",
            actionDisplayName: "Determination deadline changed",
            user,
            utcNow,
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
    /// RA-601/RA-611: compute <c>clock.TargetDuration + additionalDuration</c> without
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

    /// <summary>
    /// RA-611: the Europe/London calendar date of a UTC instant. The instant is
    /// re-stamped as UTC first because SLA clocks deserialised from Mongo can
    /// carry an Unspecified (or, historically, Local) kind, and
    /// <see cref="TimeZoneInfo.ConvertTimeFromUtc"/> rejects a Local input.
    /// </summary>
    private static DateOnly UkDateOf(DateTime utc) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(utc, DateTimeKind.Utc), s_ukTimeZone));

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
